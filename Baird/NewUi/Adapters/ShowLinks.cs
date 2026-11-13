using System;
using System.Collections.Generic;
using System.Linq;
using Baird.NewUi.Model;

namespace Baird.NewUi.Adapters
{
    /// <summary>One show an episode was played from: enough to rebuild its <see cref="CatalogueTitle"/> later.</summary>
    public sealed record ShowLink(string Key, string Name, int? Year, string? ImageUrl, string? Synopsis, List<ShowLinkSource> Sources, int? TmdbId, string? MediaType, long Seen = 0);
    public sealed record ShowLinkSource(string Service, string ItemId, string Via);

    /// <summary>
    /// Which show each episode you played belongs to, whatever service it came from. Providers hand back an episode with no
    /// pointer to its show (Disney+, Netflix, iPlayer…), so Baird notes the show when an episode is played from the show's page.
    /// Up next uses it to find what follows a finished episode. Kept on disk (<see cref="DefaultPath"/>), the newest 500 episodes.
    /// </summary>
    public sealed class ShowLinks
    {
        public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", "show-links.json");
        public const int Keep = 500;

        private readonly string? _path;
        private Dictionary<string, ShowLink>? _links;
        private readonly object _gate = new();
        private long _seen; // ordering of use: Dictionary order isn't insertion order once entries are removed

        public ShowLinks(string? path) => _path = path;

        private Dictionary<string, ShowLink> Load()
        {
            if (_links != null) return _links;
            try
            {
                _links = _path != null && System.IO.File.Exists(_path)
                    ? System.Text.Json.JsonSerializer.Deserialize(System.IO.File.ReadAllText(_path), BairdJsonContext.Default.DictionaryStringShowLink) ?? new()
                    : new();
            }
            catch (Exception ex) { Console.WriteLine($"[ShowLinks] Couldn't read {_path}: {ex.Message}"); _links = new(); }
            return _links;
        }

        /// <summary>Notes that this episode belongs to this show (a series with somewhere to watch it, or at least a Seerr entry).</summary>
        public void Remember(string episodeId, CatalogueTitle show)
        {
            if (show.Kind != TitleKind.Series || (show.Sources.Count == 0 && show.Seerr == null)) return;
            var link = new ShowLink(show.Key, show.Name, show.Year, show.ImageUrl, show.Synopsis,
                show.Sources.Select(s => new ShowLinkSource(s.Service, s.ItemId, s.Via.ToString())).ToList(), show.Seerr?.TmdbId, show.Seerr?.MediaType);
            lock (_gate)
            {
                var links = Load();
                if (_seen == 0 && links.Count > 0) _seen = links.Values.Max(x => x.Seen);
                link = link with { Seen = ++_seen };
                links[episodeId] = link;
                while (links.Count > Keep) links.Remove(links.MinBy(x => x.Value.Seen).Key);
                Save(links);
            }
        }

        /// <summary>The show this episode was played from, or null when Baird doesn't know.</summary>
        public CatalogueTitle? TitleFor(string episodeId)
        {
            ShowLink? l;
            lock (_gate) Load().TryGetValue(episodeId, out l);
            if (l == null) return null;
            var sources = l.Sources.Select(s => new TitleSource(s.Service, s.ItemId, Enum.TryParse<PlayVia>(s.Via, out var v) ? v : PlayVia.Mpv)).ToList();
            var seerr = l.TmdbId is { } id && l.MediaType != null ? new SeerrRef(id, l.MediaType, 1) : null;
            return new CatalogueTitle(l.Key, TitleKind.Series, l.Name, l.Year, l.ImageUrl, l.Synopsis, sources, seerr);
        }

        private void Save(Dictionary<string, ShowLink> links)
        {
            if (_path == null) return;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                System.IO.File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(links, BairdJsonContext.Default.DictionaryStringShowLink));
            }
            catch (Exception ex) { Console.WriteLine($"[ShowLinks] Couldn't save {_path}: {ex.Message}"); }
        }
    }
}
