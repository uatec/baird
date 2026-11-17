using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.NewUi.Model;
using Baird.Services;
using Baird.ViewModels;

namespace Baird.NewUi.Adapters
{
    /// <summary>
    /// Deleting from the server, for good. A series or film: Radarr/Sonarr remove it and its files (through Seerr, so
    /// nothing fetches it again and it can be requested again), and Jellyfin drops it from the library at once. An
    /// episode: Jellyfin deletes its file (Sonarr still lists it as wanted, but doesn't fetch old episodes by itself).
    /// </summary>
    public sealed class Deletion
    {
        private readonly IDataService _data;
        private readonly IJellyseerrService? _seerr;
        private readonly CatalogueSearch? _search;

        public Deletion(IDataService data, IJellyseerrService? seerr, CatalogueSearch? search)
        {
            _data = data; _seerr = seerr; _search = search;
        }

        /// <summary>Something was deleted: lists that show the library should read it again.</summary>
        public event Action? Deleted;

        private IMediaDeleter? Jellyfin => _data.Providers.OfType<IMediaDeleter>().FirstOrDefault();

        /// <summary>The card's Jellyfin item (not a made-up season id), if it has one.</summary>
        public static string? JellyfinIdOf(HomeCard c)
        {
            var id = c.Title?.Sources.FirstOrDefault(s => s.Service == "Jellyfin")?.ItemId
                     ?? (CatalogueSearch.ServiceOf(c.Source) == "Jellyfin" ? c.Id : null);
            return id == null || id.Contains('|') ? null : id;
        }

        /// <summary>The card's title in Seerr, when the card says (Seerr cards, and titles Search matched in Seerr).</summary>
        public static SeerrRef? SeerrOf(HomeCard c) =>
            c.Title?.Seerr ?? (c.Source == "Seerr" && c.Id.Split(':') is ["tmdb", var type, var id] && int.TryParse(id, out var tmdb)
                ? new SeerrRef(tmdb, type, c.Badge == "get" ? 1 : 2) : null);

        public bool CanDelete(HomeCard c)
        {
            if (c.Kind is not (CardKind.Series or CardKind.Film or CardKind.Episode)) return false;
            if (JellyfinIdOf(c) != null && Jellyfin != null) return true;
            return c.Kind != CardKind.Episode && _seerr != null && SeerrOf(c) is { Status: >= 2 };
        }

        /// <summary>
        /// Can this card be discarded: a request that hasn't arrived (waiting, searching, downloading), with nothing in the library
        /// yet. Once some of it is here, what's left to discard isn't separable from it: that's a delete.
        /// </summary>
        public bool CanDiscard(HomeCard c)
        {
            if (_seerr == null || c.Kind == CardKind.Episode || c.Kind == CardKind.Channel) return false;
            if (JellyfinIdOf(c) != null || c.Title is { Sources.Count: > 0 }) return false;
            if (c.Badge is "part" or "ready" or "complete" or "partial" or "get" or "declined") return false;
            return SeerrOf(c) is { Status: 2 or 3 };
        }

        /// <summary>Cancels the request (and takes it out of Radarr/Sonarr); the library is left alone.</summary>
        public async Task<(bool Ok, string Message)> DiscardAsync(HomeCard c, CancellationToken ct = default)
        {
            var name = c.Label;
            if (_seerr == null || SeerrOf(c) is not { } seerr) return (false, $"Couldn't discard {name}: Seerr doesn't know it.");
            try
            {
                var (ok, message) = await _seerr.DiscardRequestAsync(seerr.TmdbId, seerr.MediaType, ct).ConfigureAwait(false);
                if (!ok) return (false, $"Couldn't discard {name}: {message}");
                _search?.ForgetDownloads();
                Deleted?.Invoke();
                return (true, $"Discarded {name}.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[Deletion] Discarding {name} failed: {ex.Message}");
                return (false, $"Couldn't discard {name}: {ex.Message}");
            }
        }

        public async Task<(bool Ok, string Message)> DeleteAsync(HomeCard c, CancellationToken ct = default)
        {
            var name = c.Label;
            try
            {
                var jellyfinId = JellyfinIdOf(c);
                string? problem = null;
                var removed = false;
                if (c.Kind != CardKind.Episode && _seerr != null)
                {
                    var seerr = SeerrOf(c);
                    if (seerr == null && _search != null && jellyfinId != null)
                        seerr = await _search.FindInSeerrAsync(ShowData.FromCard(c), ct).ConfigureAwait(false);
                    if (seerr is { Status: >= 2 })
                    {
                        var (ok, message) = await _seerr.RemoveMediaAsync(seerr.TmdbId, seerr.MediaType, ct).ConfigureAwait(false);
                        if (ok) removed = true; else problem = message;
                    }
                }
                // A Seerr card for something that has arrived: find Jellyfin's copy, so it leaves the library now.
                if (jellyfinId == null && c.Kind != CardKind.Episode && _search != null && SeerrOf(c) is { Status: >= 4 })
                    jellyfinId = (await _search.DownloadsAsync(name, ct).ConfigureAwait(false))
                        .Where(h => SearchRelevance.Normalize(h.Card.Label) == SearchRelevance.Normalize(name))
                        .Select(h => JellyfinIdOf(h.Card)).FirstOrDefault(id => id != null);
                if (jellyfinId != null && Jellyfin is { } jf)
                {
                    if (await jf.DeleteItemAsync(jellyfinId, ct).ConfigureAwait(false)) removed = true;
                    else problem ??= "Jellyfin didn't delete it.";
                }
                if (!removed) return (false, $"Couldn't delete {name}: {problem ?? "it isn't on the server."}");
                _search?.ForgetDownloads();
                Deleted?.Invoke();
                return problem == null
                    ? (true, $"Deleted {name} from the server.")
                    : (true, $"Deleted {name}, but {problem}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[Deletion] {name} failed: {ex.Message}");
                return (false, $"Couldn't delete {name}: {ex.Message}");
            }
        }
    }
}
