using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Baird.NewUi.Model;
using Baird.Services;
using Baird.ViewModels;

namespace Baird.NewUi.Adapters
{
    /// <summary>
    /// A show page's data: finds where a title can be watched (the library) and what Seerr knows of it, reads its seasons
    /// and episodes, merges the two (seasons you have, partly have, have requested, or can request), and makes requests.
    /// Seerr requests whole seasons (or a film); a single episode can't be requested through it.
    /// </summary>
    public sealed partial class ShowData
    {
        private readonly IDataService _data;
        private readonly IJellyseerrService? _seerr;
        private readonly CatalogueSearch _search;

        public ShowData(IDataService data, IJellyseerrService? seerr, CatalogueSearch search)
        {
            _data = data; _seerr = seerr; _search = search;
        }

        public bool CanRequest => _seerr != null;

        /// <summary>A title from a Home card (a provider's series or film, or a Seerr "tmdb:tv:7" card).</summary>
        public static CatalogueTitle FromCard(HomeCard c)
        {
            if (c.Title != null) return c.Title;
            var kind = c.Kind switch { CardKind.Series => TitleKind.Series, CardKind.Episode => TitleKind.Episode, _ => TitleKind.Film };
            if (c.Source == "Seerr" && c.Id.Split(':') is ["tmdb", var type, var id] && int.TryParse(id, out var tmdb))
                return new CatalogueTitle(c.Id, kind, c.Label, CatalogueSearch.YearOf(c.Sublabel), c.ImageUrl, c.Synopsis, Array.Empty<TitleSource>(),
                    new SeerrRef(tmdb, type, c.Badge == "get" ? 1 : 5));
            return new CatalogueTitle($"{kind}:{c.Id}", kind, c.Label, null, c.ImageUrl, c.Synopsis,
                new[] { new TitleSource(CatalogueSearch.ServiceOf(c.Source), c.Id, c.Via, c.StreamUrl, c.Resume, c.Progress) });
        }

        private static readonly Regex Number = new(@"\d+", RegexOptions.Compiled);

        /// <summary>"Season 2" / "Series 2" → 2; "Specials" → 0; otherwise its position.</summary>
        public static int SeasonNumberOf(string name, int position)
        {
            if (name.Contains("special", StringComparison.OrdinalIgnoreCase)) return 0;
            var m = Number.Match(name);
            return m.Success && int.TryParse(m.Value, out var n) ? n : position;
        }

        /// <summary>
        /// Everything the show page shows: where it streams, its seasons and episodes merged with Seerr's state.
        /// Reported as it's learnt (<paramref name="progress"/>, in order): a title known only to Seerr shows Seerr's
        /// view at once, marked as still checking the library, then each library source that has it as it answers
        /// (each provider is asked separately; a slow one doesn't hold the others back). Returns the final details.
        /// </summary>
        public async Task<ShowDetails> DetailsAsync(CatalogueTitle title, Action<ShowDetails>? progress = null, CancellationToken ct = default)
        {
            var t = title;
            var searching = t.Sources.Count == 0 && t.Kind != TitleKind.Episode;
            var sources = searching ? _search.SearchSources(t.Name, ct) : Array.Empty<(string Source, Task<IReadOnlyList<MediaItemViewModel>?> Results)>();

            if (t.Seerr == null) t = t with { Seerr = await _search.FindInSeerrAsync(t, ct).ConfigureAwait(false) };
            var status = await StatusAsync(t, ct).ConfigureAwait(false);
            if (status != null && t.Seerr != null) t = t with { Seerr = t.Seerr with { Status = status.Status } };

            var remaining = sources.Select(s => s.Results).ToList();
            var found = new List<MediaItemViewModel>();
            var details = await ComposeAsync(t, status, remaining.Count > 0, ct).ConfigureAwait(false);
            progress?.Invoke(details);
            while (remaining.Count > 0)
            {
                var done = await Task.WhenAny(remaining).ConfigureAwait(false);
                remaining.Remove(done);
                ct.ThrowIfCancellationRequested();
                if (done.IsCompletedSuccessfully && done.Result is { } items) found.AddRange(items);
                var match = CatalogueSearch.Group(found, Array.Empty<JellyseerrSearchResult>()).Titles.FirstOrDefault(x => CatalogueSearch.Matches(x, t));
                var changed = match != null && !match.Sources.Select(s => s.ItemId).SequenceEqual(t.Sources.Select(s => s.ItemId));
                if (changed) t = t with { Sources = match!.Sources, ImageUrl = t.ImageUrl ?? match.ImageUrl, Synopsis = t.Synopsis ?? match.Synopsis, Year = t.Year ?? match.Year };
                if (!changed && remaining.Count > 0) continue;
                details = changed ? await ComposeAsync(t, status, remaining.Count > 0, ct).ConfigureAwait(false) : details with { CheckingLibrary = false };
                progress?.Invoke(details);
            }
            return details;
        }

        private async Task<ShowDetails> ComposeAsync(CatalogueTitle t, JellyseerrMediaStatus? status, bool checking, CancellationToken ct)
        {
            if (t.Kind != TitleKind.Series)
            {
                var film = t.Sources.Count > 0 || status?.Status == 5 ? SeasonState.Available
                    : status?.Status is 2 or 3 or 4 ? SeasonState.Requested : SeasonState.Missing;
                return new ShowDetails(t, Array.Empty<Season>(), film, status?.Progress(), checking);
            }
            var library = await LibrarySeasonsAsync(t, ct).ConfigureAwait(false);
            return new ShowDetails(t, Merge(library, status, t.Best?.Service), CheckingLibrary: checking);
        }

        private async Task<JellyseerrMediaStatus?> StatusAsync(CatalogueTitle t, CancellationToken ct)
        {
            if (_seerr == null || t.Seerr == null) return null;
            try { return await _seerr.GetMediaStatusAsync(t.Seerr.TmdbId, t.Seerr.MediaType, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Console.WriteLine($"[ShowData] Seerr status failed: {ex.Message}"); return null; }
        }

        public sealed record LibrarySeason(int Number, string Label, IReadOnlyList<MediaItemViewModel> Episodes, string? Service = null);

        /// <summary>
        /// The show's seasons in the library, across its sources (the best source wins a season both have): each source's
        /// season folders, or one season when it lists only episodes.
        /// </summary>
        public async Task<IReadOnlyList<LibrarySeason>> LibrarySeasonsAsync(CatalogueTitle t, CancellationToken ct = default)
        {
            var perSource = await Task.WhenAll(t.Sources.Select(src => SeasonsFromAsync(t, src))).ConfigureAwait(false);
            var seasons = new List<LibrarySeason>();
            foreach (var list in perSource)
                foreach (var s in list)
                    if (seasons.All(x => x.Number != s.Number)) seasons.Add(s);
            return seasons.OrderBy(s => s.Number == 0 ? int.MaxValue : s.Number).ToList();
        }

        private async Task<IReadOnlyList<LibrarySeason>> SeasonsFromAsync(CatalogueTitle t, TitleSource src)
        {
            try
            {
                var children = (await _data.GetChildrenAsync(src.ItemId).ConfigureAwait(false)).ToList();
                if (children.Count == 0) return Array.Empty<LibrarySeason>();
                var folders = children.Where(c => c.Type == Baird.Services.MediaType.Brand).ToList();
                if (folders.Count == 0) return new[] { new LibrarySeason(1, "Season 1", children, src.Service) };
                return await Task.WhenAll(folders.Select(async (f, i) =>
                {
                    var eps = (await _data.GetChildrenAsync(f.Id).ConfigureAwait(false)).Where(e => e.Type != Baird.Services.MediaType.Brand).ToList();
                    var n = SeasonNumberOf(f.Name, i + 1);
                    return new LibrarySeason(n, n == 0 ? "Specials" : $"Season {n}", eps, src.Service);
                })).ConfigureAwait(false);
            }
            catch (Exception ex) { Console.WriteLine($"[ShowData] Seasons of {t.Name} from {src.Service} failed: {ex.Message}"); return Array.Empty<LibrarySeason>(); }
        }

        private static Episode EpisodeOf(MediaItemViewModel m, int season, int number)
        {
            var src = CatalogueSearch.SourceOf(m);
            var watched = m.History?.IsFinished == true;
            var mins = m.Duration > TimeSpan.Zero ? $"{m.Duration.TotalMinutes:0} min" : null;
            var name = string.IsNullOrWhiteSpace(m.Subtitle) ? m.Name : m.Subtitle;
            return new Episode(season, number, $"{number}. {name}", mins, string.IsNullOrEmpty(m.ImageUrl) ? null : m.ImageUrl, EpisodeState.Available,
                src, watched, Synopsis: string.IsNullOrWhiteSpace(m.Synopsis) ? null : m.Synopsis, LastWatched: m.History?.LastWatched);
        }

        /// <summary>
        /// Library seasons merged with Seerr's: a season you have (all of it, or <c>n of m</c>), one requested (with its
        /// download percentage), or one you can request. Episodes Seerr counts beyond those in the library are listed as
        /// missing or downloading.
        /// </summary>
        public static IReadOnlyList<Season> Merge(IReadOnlyList<LibrarySeason> library, JellyseerrMediaStatus? status, string? service)
        {
            var numbers = library.Select(s => s.Number).Concat(status?.Seasons.Select(s => s.Number) ?? Enumerable.Empty<int>())
                .Distinct().OrderBy(n => n == 0 ? int.MaxValue : n).ToList();
            var seasons = new List<Season>();
            foreach (var n in numbers)
            {
                var lib = library.FirstOrDefault(s => s.Number == n);
                var seerr = status?.Seasons.FirstOrDefault(s => s.Number == n);
                var seerrState = seerr?.Status ?? 1;
                var onTheWay = seerrState is 2 or 3;
                EpisodeState Pending(int ep) => onTheWay ? EpisodeState.Requested : EpisodeState.Missing;
                double? Dl(int ep) => onTheWay ? status?.Progress(n, ep) ?? status?.Progress(n) : null;
                var eps = new List<Episode>();
                if (lib != null) eps.AddRange(lib.Episodes.Select((m, i) => EpisodeOf(m, n, i + 1)));
                var count = seerr?.EpisodeCount ?? 0;
                for (var e = eps.Count + 1; e <= count; e++)
                    eps.Add(new Episode(n, e, $"{e}. Episode {e}", null, null, Pending(e), Download: Dl(e)));

                var label = n == 0 ? "Specials" : $"Season {n}";
                if (lib != null && lib.Episodes.Count > 0)
                {
                    var partial = count > lib.Episodes.Count;
                    seasons.Add(new Season(n, label, partial ? SeasonState.Partial : SeasonState.Available, lib.Service ?? service, eps,
                        count > 0 ? count : lib.Episodes.Count, lib.Episodes.Count, partial && onTheWay ? status?.Progress(n) : null));
                }
                else if (seerrState is 4 or 5)
                    // Seerr says it's in the library, but the library didn't list it (yet): say so; nothing to request.
                    seasons.Add(new Season(n, label, SeasonState.Requested, null, eps, count, 0, null));
                else
                    seasons.Add(new Season(n, label, onTheWay ? SeasonState.Requested : SeasonState.Missing, null, eps, count, 0, onTheWay ? status?.Progress(n) : null));
            }
            return seasons;
        }

        /// <summary>
        /// Ask Seerr for a film, a show's seasons (null = all missing), and say plainly what happened.
        /// Seerr has no per-episode requests, so an episode is requested as its season.
        /// </summary>
        public async Task<(bool Ok, string Message)> RequestAsync(ShowDetails show, IReadOnlyList<int>? seasons, CancellationToken ct = default)
        {
            var t = show.Title;
            if (_seerr == null || t.Seerr == null) return (false, $"Baird can't find {t.Name} in Seerr, so it can't be requested.");
            var wanted = t.Kind == TitleKind.Series ? (seasons ?? show.MissingSeasons.ToList()) : null;
            if (wanted is { Count: 0 }) return (false, "Nothing left to request: every season is in the library or on its way.");
            try
            {
                var r = await _seerr.RequestSeasonsAsync(t.Seerr.TmdbId, t.Seerr.MediaType, wanted, ct).ConfigureAwait(false);
                if (!r.Success) return (false, $"Seerr didn't take the request: {r.Message}");
                var what = wanted == null ? t.Name : $"{t.Name}, {ShowDetails.Label(wanted).ToLowerInvariant()}";
                return (true, $"Requested {what}. It downloads to Jellyfin and appears in Up next when it's ready.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[ShowData] Request failed: {ex.Message}");
                return (false, "The request didn't reach Seerr. Try again in a moment.");
            }
        }

        /// <summary>Is the title (its best source) in the watchlist?</summary>
        public bool InWatchlist(CatalogueTitle t) => t.Best is { } b && _data.IsOnWatchlist(b.ItemId);

        /// <summary>Add or remove the title (its best source) from the watchlist; returns whether it's now in it.</summary>
        public async Task<bool> ToggleWatchlistAsync(CatalogueTitle t)
        {
            if (t.Best is not { } b) return false;
            if (_data.IsOnWatchlist(b.ItemId)) { await _data.RemoveFromWatchlistAsync(b.ItemId).ConfigureAwait(false); return false; }
            var item = await _data.GetItemAsync(b.ItemId).ConfigureAwait(false);
            if (item == null) return false;
            await _data.AddToWatchlistAsync(item).ConfigureAwait(false);
            return true;
        }
    }
}
