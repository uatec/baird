using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Baird.NewUi.Model;
using Baird.Services;

namespace Baird.NewUi.Adapters
{
    /// <summary>
    /// A picture for a broadcast programme. First the guide's own (TvHeadend's XMLTV source has one for about half);
    /// otherwise TMDB via Seerr, matched on the exact title (broadcast decorations like "Classic" or "New:" ignored),
    /// preferring a landscape backdrop. Answers and misses are remembered on disk, so each title is looked up once.
    /// </summary>
    public sealed class ProgrammeArt
    {
        private const string Miss = "";
        private static readonly Regex Decorations = new(@"^(classic|new|live|brand new)\s*:?\s+|\s*\((r|repeat|new)\)\s*$|\s*\.\.\.$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly IJellyseerrService _seerr;
        private readonly string? _cachePath;
        private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _saveLock = new();

        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", "programme-art.json");

        public ProgrammeArt(IJellyseerrService seerr, string? cachePath)
        {
            _seerr = seerr; _cachePath = cachePath;
            try
            {
                if (cachePath != null && File.Exists(cachePath)
                    && JsonSerializer.Deserialize(File.ReadAllText(cachePath), BairdJsonContext.Default.DictionaryStringString) is { } saved)
                    foreach (var kv in saved) _cache[kv.Key] = kv.Value;
            }
            catch (Exception ex) { Console.WriteLine($"[ProgrammeArt] Couldn't read the cache: {ex.Message}"); }
        }

        /// <summary>The title as a catalogue would name it.</summary>
        public static string Clean(string title)
        {
            var t = title.Trim();
            for (var i = 0; i < 3; i++) t = Decorations.Replace(t, "").Trim();
            return t;
        }

        private static string Key(string s) => new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        public async Task<string?> ForAsync(Programme p, CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(p.ImageUrl)) return p.ImageUrl;
            if (p.NoInfo || string.IsNullOrWhiteSpace(p.Title)) return null;
            var title = Clean(p.Title);
            if (_cache.TryGetValue(title, out var known)) return known == Miss ? null : known;

            string found = Miss;
            try
            {
                var results = await _seerr.SearchAsync(title, 1, ct).ConfigureAwait(false);
                var key = Key(title);
                var match = results.Where(r => Key(r.Title) == key)
                    .OrderBy(r => r.MediaType == "tv" ? 0 : 1)            // broadcasts are mostly series
                    .ThenBy(r => string.IsNullOrEmpty(r.BackdropPath) ? 1 : 0)
                    .FirstOrDefault();
                if (match?.BackdropPath is { Length: > 0 } b) found = $"https://image.tmdb.org/t/p/w1280{b}";
                else if (match?.PosterPath is { Length: > 0 } pp) found = $"https://image.tmdb.org/t/p/w780{pp}";
            }
            catch (OperationCanceledException) { return null; } // not an answer: try again next time
            catch (Exception ex) { Console.WriteLine($"[ProgrammeArt] Lookup for {title} failed: {ex.Message}"); return null; }

            _cache[title] = found;
            Save();
            return found == Miss ? null : found;
        }

        private void Save()
        {
            if (_cachePath == null) return;
            try
            {
                lock (_saveLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
                    File.WriteAllText(_cachePath, JsonSerializer.Serialize(new Dictionary<string, string>(_cache), BairdJsonContext.Default.DictionaryStringString));
                }
            }
            catch (Exception ex) { Console.WriteLine($"[ProgrammeArt] Couldn't save the cache: {ex.Message}"); }
        }
    }
}
