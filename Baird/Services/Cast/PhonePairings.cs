using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Baird.Services.Cast
{
    /// <summary>
    /// The phones allowed to send Baird something to play. Each "Connect a phone" screen issues a token (in its pairing QR);
    /// a phone that scanned it sends it with every link. Tokens never used are dropped after a day, so pairing screens that
    /// nobody scanned don't pile up. Kept in ~/.baird/phones.json; "Forget phones" in Settings empties it.
    /// </summary>
    public sealed class PhonePairings
    {
        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", "phones.json");
        public static readonly TimeSpan UnusedLifetime = TimeSpan.FromDays(1);

        private sealed record Entry(string Token, DateTimeOffset Issued, DateTimeOffset? LastUsed);

        private readonly string? _path;
        private readonly Func<DateTimeOffset> _now;
        private readonly object _gate = new();
        private List<Entry> _entries;

        public PhonePairings(string? path, Func<DateTimeOffset> now)
        {
            _path = path; _now = now;
            _entries = Load(path);
        }

        /// <summary>Phones that have sent something (tokens issued but never used don't count).</summary>
        public int Connected { get { lock (_gate) return _entries.Count(e => e.LastUsed != null); } }

        /// <summary>A new token for a pairing QR.</summary>
        public string Issue()
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            lock (_gate)
            {
                Prune();
                _entries.Add(new Entry(token, _now(), null));
                Save();
            }
            return token;
        }

        /// <summary>Is this a phone Baird knows? Marks it used.</summary>
        public bool Accept(string? token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            var given = Encoding.UTF8.GetBytes(token);
            lock (_gate)
            {
                Prune();
                var i = _entries.FindIndex(e => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(e.Token), given));
                if (i < 0) return false;
                _entries[i] = _entries[i] with { LastUsed = _now() };
                Save();
                return true;
            }
        }

        public void ForgetAll()
        {
            lock (_gate) { _entries.Clear(); Save(); }
        }

        private void Prune() => _entries.RemoveAll(e => e.LastUsed == null && _now() - e.Issued > UnusedLifetime);

        private static List<Entry> Load(string? path)
        {
            try
            {
                if (path == null || !File.Exists(path)) return new();
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.EnumerateArray().Select(e => new Entry(
                    e.GetProperty("token").GetString()!,
                    e.GetProperty("issued").GetDateTimeOffset(),
                    e.TryGetProperty("lastUsed", out var u) && u.ValueKind == JsonValueKind.String ? u.GetDateTimeOffset() : null)).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PhonePairings] Couldn't read {path}: {ex.Message}");
                return new();
            }
        }

        private void Save()
        {
            if (_path == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                using var stream = new MemoryStream();
                using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    w.WriteStartArray();
                    foreach (var e in _entries)
                    {
                        w.WriteStartObject();
                        w.WriteString("token", e.Token);
                        w.WriteString("issued", e.Issued);
                        if (e.LastUsed is { } used) w.WriteString("lastUsed", used);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                var tmp = _path + ".tmp";
                File.WriteAllBytes(tmp, stream.ToArray());
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) { Console.WriteLine($"[PhonePairings] Couldn't save {_path}: {ex.Message}"); }
        }
    }
}
