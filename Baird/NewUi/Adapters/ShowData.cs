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
    /// <summary>A show page learnt nothing at all within its budget (nothing to show but an error with a retry).</summary>
    public sealed class ShowLoadTimeoutException : Exception
    {
        public ShowLoadTimeoutException(string title) : base($"{title} didn't load in time") { }
    }

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

        private readonly TimeSpan _pieceTimeout;

        /// <summary>How long a page may take to learn everything before it settles for what it has.</summary>
        public TimeSpan Budget { get; }

        /// <summary>How long what was learnt of availability (Seerr status, a source's seasons) is fresh; after it, it is still served while it is refreshed.</summary>
        public static readonly TimeSpan AvailabilityFresh = TimeSpan.FromSeconds(60);
        private readonly StaleCache<JellyseerrMediaStatus?> _statusCache;
        private readonly StaleCache<IReadOnlyList<LibrarySeason>> _seasonCache;
        private readonly Dictionary<string, DateTimeOffset> _refreshing = new(); // key -> when its refresh was requested
        private readonly Func<DateTimeOffset> _clock;
        /// <summary>A refresh asked for this long ago and never finished is taken to be lost (a sink that dropped it), and may be asked for again.</summary>
        private static readonly TimeSpan RefreshLost = TimeSpan.FromMinutes(2);

        /// <summary>
        /// When set, a stale answer's background refresh is handed here (key, work) instead of being run by ShowData itself.
        /// The sink MUST run the work (it never faults); one that drops it only delays that key's next refresh by two minutes.
        /// </summary>
        public Action<string, Func<CancellationToken, Task>>? RefreshSink { get; set; }

        /// <summary>Metadata kept between runs (names, art, seasons, episodes; never sources or availability), when there is a store.</summary>
        private readonly IShowStore? _store;
        /// <summary>How long a title's kept metadata is fresh; after it, it is still served while the live answers refresh it.</summary>
        public static readonly TimeSpan MetadataFresh = TimeSpan.FromDays(7);

        public ShowData(IDataService data, IJellyseerrService? seerr, CatalogueSearch search, TimeSpan? pieceTimeout = null, TimeSpan? budget = null, Func<DateTimeOffset>? clock = null, IShowStore? store = null)
        {
            _store = store;
            var now = _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _statusCache = new(AvailabilityFresh, now);
            _seasonCache = new(AvailabilityFresh, now);
            _data = data; _seerr = seerr; _search = search;
            _pieceTimeout = pieceTimeout ?? TimeSpan.FromSeconds(8);
            Budget = budget ?? TimeSpan.FromSeconds(8);
            // Watching something changes Resume/Next: what was learnt before is out of date.
            _data.HistoryUpdated += (_, _) => Forget();
        }

        // ------------------------------------------------------------------ shared, briefly kept details
        /// <summary>One title's details being learnt (or learnt): shared by the prefetch and the page, kept briefly.</summary>
        private sealed class Entry
        {
            public Task<ShowDetails> Task = null!;
            public readonly CancellationTokenSource Cts = new();
            public ShowDetails? Latest;
            public long Seq;                              // reports so far: Latest is report Seq (0 = the kept record, or nothing)
            public Action<ShowDetails, long>? Progress;
            public int Waiters;
            public volatile bool Stored;                  // its live result was kept in the store
            public DateTimeOffset At = DateTimeOffset.UtcNow;
        }
        private readonly Dictionary<string, Entry> _cache = new();
        public static readonly TimeSpan Keep = TimeSpan.FromSeconds(90);

        /// <summary>Drop what's been learnt (after a request, or watching something).</summary>
        public void Forget()
        {
            _statusCache.Clear(); _seasonCache.Clear();
            lock (_cache) foreach (var k in _cache.Where(e => e.Value.Task.IsCompleted).Select(e => e.Key).ToList()) _cache.Remove(k);
        }

        /// <summary>
        /// Answer from the availability cache: a fresh hit as is; a stale hit at once, with a refresh started in the background;
        /// otherwise ask (a failed answer is told to <paramref name="outcome"/>). <paramref name="fetch"/> says whether it really answered (a timeout or failure is <c>Ok = false</c> with the
        /// fallback): only a real answer is kept, so a bad moment is never served back for ever. <paramref name="fresh"/> skips the cache.
        /// </summary>
        private async Task<T> CachedAsync<T>(StaleCache<T> cache, string key, bool fresh, Func<CancellationToken, Task<(bool Ok, T Value)>> fetch, CancellationToken ct, Outcome? outcome = null)
        {
            if (!fresh && cache.TryGet(key, out var kept, out var stale))
            {
                if (stale) Refresh(key, async c => { var r = await fetch(c).ConfigureAwait(false); if (r.Ok) cache.Put(key, r.Value); });
                return kept;
            }
            var answer = await fetch(ct).ConfigureAwait(false);
            if (answer.Ok) cache.Put(key, answer.Value);
            else outcome?.Fail();
            return answer.Value;
        }

        /// <summary>
        /// Refresh one cached answer in the background (at most one per key); never faults anything. The one place that changes when refreshes go
        /// through a queue. A <see cref="RefreshSink"/> must run the work: if it doesn't, the key is free to be asked for again after <see cref="RefreshLost"/>.
        /// </summary>
        private void Refresh(string key, Func<CancellationToken, Task> work)
        {
            lock (_refreshing)
            {
                var asked = _clock();
                if (_refreshing.TryGetValue(key, out var since) && asked - since < RefreshLost) return;
                _refreshing[key] = asked;
            }
            async Task Run(CancellationToken ct)
            {
                try { await work(ct).ConfigureAwait(false); }
                catch (Exception ex) { Console.WriteLine($"[ShowData] Refreshing {key} failed: {ex.Message}"); }
                finally { lock (_refreshing) _refreshing.Remove(key); }
            }
            var sink = RefreshSink;
            if (sink != null) sink(key, Run);
            else _ = Task.Run(() => Run(CancellationToken.None));
        }

        /// <summary>Has this title's page already been worked out (or is it being)? (tests)</summary>
        public bool IsKnown(CatalogueTitle t) { lock (_cache) return _cache.ContainsKey(t.Key); }

        /// <summary>
        /// The show page's details (see <see cref="ComputeAsync"/>), shared: a page opened while its prefetch is running
        /// joins it (and gets what's known so far at once), and one opened shortly after gets the result straight away.
        /// <paramref name="fresh"/> asks again (after a request; polling a download). The work stops when nobody waits for it.
        /// With a kept record (<see cref="IShowStore"/>) and not <paramref name="fresh"/>, the record is reported first, before anything
        /// is asked (availability unknown: <see cref="ShowDetails.CheckingLibrary"/>, the title's own sources only); the live answers
        /// follow and, once complete, are kept in its place. With a record, running out of budget (or a failed compute) returns what's
        /// known, never an error: the record itself (still checking) when nothing live was reported.
        /// </summary>
        public async Task<ShowDetails> DetailsAsync(CatalogueTitle title, Action<ShowDetails>? progress = null, CancellationToken ct = default, bool fresh = false)
        {
            var stored = Kept(title);
            var fromStore = stored == null ? null : FromStore(title, stored);
            // Each waiter sees reports in order: one that arrives late (the record, after a live report) is dropped.
            var shown = -1L; var gate = new object();
            Action<ShowDetails, long>? deliver = progress == null ? null : (d, seq) => { lock (gate) { if (seq <= shown) return; shown = seq; progress(d); } };
            Entry e;
            ShowDetails? latest;
            long latestSeq;
            lock (_cache)
            {
                e = Join(title, fresh, stored, renew: fresh);
                if (!fresh && fromStore != null && e.Latest == null && !e.Task.IsCompleted) e.Latest = fromStore;
                e.Waiters++;
                if (deliver != null) e.Progress += deliver;
                latest = e.Task.IsCompleted ? null : e.Latest;
                latestSeq = e.Seq;
            }
            if (latest != null) deliver?.Invoke(latest, latestSeq);
            // What to settle for: the record as it is (availability unknown, still "–") while nothing live has been reported, else
            // the latest live report, taken as final.
            ShowDetails? Known()
            {
                lock (_cache) return e.Seq == 0 ? e.Latest ?? fromStore : e.Latest! with { CheckingLibrary = false };
            }
            try
            {
                // A hard budget: when it passes, settle for what's been learnt (or fail if nothing has). The work itself carries on
                // (a retry joins it); only the waiting stops.
                var waited = e.Task.WaitAsync(ct);
                using var stop = new CancellationTokenSource();
                var first = await Task.WhenAny(waited, Task.Delay(Budget, stop.Token)).ConfigureAwait(false);
                if (first == waited || e.Task.IsCompletedSuccessfully)
                {
                    stop.Cancel();
                    try { return await waited.ConfigureAwait(false); }
                    catch (Exception ex) when (stored != null && !ct.IsCancellationRequested && Known() is { } kept)
                    {
                        Console.WriteLine($"[ShowData] {title.Name}: {ex.Message} - showing what was kept");
                        return kept;
                    }
                }
                if (Known() is { } known) return known;
                Console.WriteLine($"[ShowData] {title.Name}: nothing learnt within {Budget.TotalSeconds:0.#} s");
                throw new ShowLoadTimeoutException(title.Name);
            }
            finally { Leave(e, title, deliver, ct); }
        }

        /// <summary>
        /// For the warming queue: when the title has no kept record, or one older than <see cref="MetadataFresh"/>, work out its details
        /// (joining a page doing so) and keep them; a fresh record means no network at all. Returns whether a live result was kept: false
        /// for a fresh record, no store, or a compute that timed out or failed (which leaves the old record as it was). Never throws but
        /// for cancellation of <paramref name="ct"/>.
        /// </summary>
        public async Task<bool> EnsureFreshAsync(CatalogueTitle title, CancellationToken ct = default)
        {
            if (_store == null) return false;
            var stored = Kept(title);
            if (stored != null && _clock() - stored.Stamp < MetadataFresh) return false;
            Entry e;
            lock (_cache)
            {
                // An answer already worked out is no refresh: ask again unless one is in flight.
                e = Join(title, false, stored, renew: _cache.TryGetValue(title.Key, out var cur) && cur.Task.IsCompleted);
                e.Waiters++;
            }
            try
            {
                await e.Task.WaitAsync(ct).ConfigureAwait(false);
                return e.Stored;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[ShowData] Refreshing {title.Name} failed: {ex.Message}");
                return false;
            }
            finally { Leave(e, title, null, ct); }
        }

        /// <summary>
        /// For the warmer: the title's kept record as details, exactly as <see cref="DetailsAsync"/> first reports it (names, art,
        /// seasons and episodes; availability unknown, <see cref="ShowDetails.CheckingLibrary"/>). Null when nothing is kept (or there
        /// is no store). Synchronous and from the store only: never any network.
        /// </summary>
        public ShowDetails? Stored(CatalogueTitle title) => Kept(title) is { } s ? FromStore(title, s) : null;

        /// <summary>
        /// For the warmer: makes one season of a kept record fully named and returns its episodes' still URLs (empty when the record
        /// or the season isn't kept). Only a Seerr-backed series whose kept season has nothing but placeholder episodes ("3. Episode 3",
        /// no still, no synopsis) asks Seerr for the season (bounded by the piece timeout); its names, stills and synopses fill those
        /// placeholders (never a real name) and the record is kept again, with its stamp unchanged (only that season was learnt).
        /// A timeout or failure keeps nothing. A season already named, or a title Seerr doesn't know, costs no network at all.
        /// Never throws but for cancellation of <paramref name="ct"/>.
        /// </summary>
        public async Task<IReadOnlyList<string>> EnsureSeasonAsync(CatalogueTitle title, int seasonNumber, CancellationToken ct = default)
        {
            var kept = Kept(title);
            var season = kept?.Seasons.FirstOrDefault(s => s.Number == seasonNumber);
            if (kept == null || season == null) return Array.Empty<string>();
            var r = title.Seerr ?? kept.Title.Seerr;
            if (_store == null || _seerr == null || r is not { MediaType: "tv" } || !Unnamed(season)) return Stills(season);
            var seerr = _seerr;
            var answer = await Deadline.Within<(bool Ok, JellyseerrSeasonDetails? Value)>(async c => (true, await seerr.GetSeasonAsync(r.TmdbId, seasonNumber, c).ConfigureAwait(false)),
                (false, null), _pieceTimeout, $"[ShowData] Seerr season {seasonNumber}", ct).ConfigureAwait(false);
            if (!answer.Ok || answer.Value is not { } named) return Stills(season);
            // Read again: a page may have kept a newer record while Seerr answered. Only placeholders are ever filled.
            var latest = Kept(title);
            var now = latest?.Seasons.FirstOrDefault(s => s.Number == seasonNumber);
            if (latest == null || now == null) return Stills(season);
            var filled = now with
            {
                Episodes = now.Episodes.Select(e => Placeholder(e) && named.Episodes.FirstOrDefault(x => x.Number == e.Number) is { } info && !string.IsNullOrWhiteSpace(info.Name)
                    ? e with { Label = $"{e.Number}. {info.Name}", ImageUrl = string.IsNullOrEmpty(info.StillUrl) ? null : info.StillUrl, Synopsis = string.IsNullOrWhiteSpace(info.Overview) ? null : info.Overview }
                    : e).ToList(),
            };
            if (filled.Episodes.SequenceEqual(now.Episodes)) return Stills(now);
            try { _store.Put(title.Key, latest with { Seasons = latest.Seasons.Select(s => s == now ? filled : s).ToList() }); }
            catch (Exception ex) { Console.WriteLine($"[ShowData] Keeping {title.Name} season {seasonNumber} failed: {ex.Message}"); }
            return Stills(filled);
        }

        /// <summary>An episode only numbered ("3. Episode 3": no still, no synopsis), as <see cref="Merge"/> lists one Seerr hasn't named.</summary>
        private static bool Placeholder(StoredEpisode e) => e.ImageUrl == null && e.Synopsis == null && e.Label == $"{e.Number}. Episode {e.Number}";

        /// <summary>A kept season with episodes, every one of them a placeholder.</summary>
        private static bool Unnamed(StoredSeason s) => s.Episodes.Count > 0 && s.Episodes.All(Placeholder);

        /// <summary>
        /// A page's season whose episodes are all placeholders ("3. Episode 3", nothing to play, no still, no synopsis): its names aren't
        /// loaded yet. (The warmer lights such a season's tab urgently.)
        /// </summary>
        public static bool Unnamed(Season s) => s.Episodes.Count > 0 &&
            s.Episodes.All(e => e.Source == null && e.ImageUrl == null && e.Synopsis == null && e.Label == $"{e.Number}. Episode {e.Number}");

        private static IReadOnlyList<string> Stills(StoredSeason s) => s.Episodes.Select(e => e.ImageUrl).OfType<string>().Where(u => u.Length > 0).ToList();

        /// <summary>The title's entry, started when there's none (or it failed) or <paramref name="renew"/> asks. Caller holds the lock.</summary>
        private Entry Join(CatalogueTitle title, bool fresh, StoredShow? stored, bool renew)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var k in _cache.Where(x => x.Value.Task.IsCompleted && now - x.Value.At > Keep).Select(x => x.Key).ToList()) _cache.Remove(k);
            if (!renew && _cache.TryGetValue(title.Key, out var e) && !e.Task.IsCanceled && !e.Task.IsFaulted) return e;
            var entry = new Entry();
            void Report(ShowDetails d)
            {
                d = Fill(d, stored);
                Action<ShowDetails, long>? listeners;
                long seq;
                lock (_cache) { entry.Latest = d; seq = ++entry.Seq; listeners = entry.Progress; }
                listeners?.Invoke(d, seq);
            }
            // A card that knows nothing of Seerr (Jellyfin's) uses the record's entry: no lookup, and a hung one can't lose it. Status is asked afresh.
            var start = title.Seerr == null && stored?.Title.Seerr is { } kept ? title with { Seerr = kept } : title;
            entry.Task = Task.Run(() => RunAsync(start, Report, fresh, stored, entry));
            _cache[title.Key] = entry;
            return entry;
        }

        private void Leave(Entry e, CatalogueTitle title, Action<ShowDetails, long>? deliver, CancellationToken ct)
        {
            lock (_cache)
            {
                if (deliver != null) e.Progress -= deliver;
                if (--e.Waiters == 0 && !e.Task.IsCompleted && ct.IsCancellationRequested)
                {
                    e.Cts.Cancel(); // nobody wants it any more (the lit card moved on)
                    if (_cache.TryGetValue(title.Key, out var cur) && cur == e) _cache.Remove(title.Key);
                }
            }
        }

        /// <summary>Work out the details live and, when every piece answered, keep them (metadata only) for next time.</summary>
        private async Task<ShowDetails> RunAsync(CatalogueTitle title, Action<ShowDetails> report, bool fresh, StoredShow? stored, Entry entry)
        {
            var outcome = new Outcome();
            var d = Fill(await ComputeAsync(title, report, fresh, entry.Cts.Token, outcome).ConfigureAwait(false), stored);
            if (_store != null && !outcome.Failed && !d.CheckingLibrary && !entry.Cts.IsCancellationRequested)
            {
                try { _store.Put(title.Key, ToStored(d, _clock())); entry.Stored = true; }
                catch (Exception ex) { Console.WriteLine($"[ShowData] Keeping {title.Name} failed: {ex.Message}"); }
            }
            return d;
        }

        /// <summary>Whether some piece of a compute timed out or failed (its result is then not worth keeping).</summary>
        private sealed class Outcome
        {
            private volatile bool _failed;
            public bool Failed => _failed;
            public void Fail() => _failed = true;
        }

        private StoredShow? Kept(CatalogueTitle title)
        {
            if (_store == null) return null;
            try { return _store.TryGet(title.Key, out var s) ? s : null; }
            catch (Exception ex) { Console.WriteLine($"[ShowData] Reading {title.Name} from the store failed: {ex.Message}"); return null; }
        }

        /// <summary>
        /// A page from a kept record: names, art, synopses, seasons and episodes, availability unknown (every season and episode
        /// Missing under <see cref="ShowDetails.CheckingLibrary"/>, which reads "–"). Sources only when the title carries them (a card that can play).
        /// </summary>
        private static ShowDetails FromStore(CatalogueTitle t, StoredShow s)
        {
            var k = s.Title;
            var title = t with
            {
                Year = t.Year ?? k.Year,
                ImageUrl = string.IsNullOrEmpty(t.ImageUrl) ? k.ImageUrl : t.ImageUrl,
                Synopsis = string.IsNullOrWhiteSpace(t.Synopsis) ? k.Synopsis : t.Synopsis,
                Seerr = t.Seerr ?? k.Seerr,
                Subtitle = t.Subtitle ?? k.Subtitle,
            };
            var seasons = s.Seasons.Select(se => new Season(se.Number, se.Label, SeasonState.Missing, null,
                se.Episodes.Select(e => new Episode(se.Number, e.Number, e.Label, e.Sublabel, e.ImageUrl, EpisodeState.Missing, Synopsis: e.Synopsis)).ToList(),
                se.EpisodeCount, 0)).ToList();
            return new ShowDetails(title, seasons, CheckingLibrary: true);
        }

        /// <summary>What's kept of live details: no sources, no item to focus, no availability (the store drops them too).</summary>
        private static StoredShow ToStored(ShowDetails d, DateTimeOffset stamp) => new(
            d.Title with { Sources = Array.Empty<TitleSource>(), FocusItemId = null, Seerr = d.Title.Seerr is { } r ? r with { Status = 0 } : null },
            d.Seasons.Select(s => new StoredSeason(s.Number, s.Label,
                s.Episodes.Select(e => new StoredEpisode(e.Number, e.Label, e.Sublabel, e.ImageUrl, e.Synopsis)).ToList(), s.EpisodeCount)).ToList(),
            stamp);

        /// <summary>Episodes the live answers only number ("3. Episode 3", nothing else known) keep the name, still and synopsis kept for them.</summary>
        private static ShowDetails Fill(ShowDetails d, StoredShow? s)
        {
            if (s == null || d.Seasons.Count == 0) return d;
            var changed = false;
            var seasons = d.Seasons.Select(season =>
            {
                var kept = s.Seasons.FirstOrDefault(x => x.Number == season.Number);
                if (kept == null) return season;
                var filled = false;
                var eps = season.Episodes.Select(e =>
                {
                    if (e.Source != null || e.ImageUrl != null || e.Synopsis != null || e.Label != $"{e.Number}. Episode {e.Number}") return e;
                    var k = kept.Episodes.FirstOrDefault(x => x.Number == e.Number);
                    if (k == null) return e;
                    filled = true;
                    return e with { Label = k.Label, ImageUrl = k.ImageUrl, Synopsis = k.Synopsis };
                }).ToList();
                if (!filled) return season;
                changed = true;
                return season with { Episodes = eps };
            }).ToList();
            return changed ? d with { Seasons = seasons } : d;
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

        /// <summary>The show an episode belongs to (as its provider lists it), or null when the episode doesn't say.</summary>
        public static CatalogueTitle? SeriesOf(Baird.Models.MediaItemData episode) =>
            string.IsNullOrEmpty(episode.SeriesId) || string.IsNullOrEmpty(episode.SeriesName) ? null
            : new CatalogueTitle($"{TitleKind.Series}:{episode.SeriesId}", TitleKind.Series, episode.SeriesName, null, null, null,
                new[] { new TitleSource(CatalogueSearch.ServiceOf(episode.Source), episode.SeriesId, HomeData.ViaOf(episode.Source)) });

        /// <summary>
        /// What to offer after you finish an episode of this show (for Up next): the episode after the last one you watched, when it
        /// can play; otherwise, when Seerr knows of later episodes you don't have, the show itself, to get ("get") or already on its way
        /// ("requested"). Null when you're part-way through an episode (it's in Up next already), the show is over, or nothing is known.
        /// </summary>
        public async Task<HomeCard?> FollowingAsync(CatalogueTitle series, CancellationToken ct = default)
        {
            var d = await DetailsAsync(series, null, ct).ConfigureAwait(false);
            if (d.CheckingLibrary) return null; // only a kept record, unverified: nothing to offer on it
            // "Resume" is in Up next already; "Play" means nothing was watched or nothing follows the last episode (Next falls back to the first).
            if (d.Next is { } n && n.Label.StartsWith("Resume ", StringComparison.Ordinal)) return null;
            if (d.Next is { } nx && nx.Label.StartsWith("Next ", StringComparison.Ordinal) && nx.Episode.Source is { } s)
            {
                var e = nx.Episode;
                return new HomeCard(CardKind.Episode, s.ItemId, e.Label, e.Code, e.ImageUrl, null, null, s.Service, s.Via, s.StreamUrl, s.Resume, Synopsis: e.Synopsis);
            }

            var all = d.Seasons.SelectMany(x => x.Episodes).ToList();
            var last = all.Where(e => e.LastWatched != null).OrderByDescending(e => e.LastWatched).FirstOrDefault();
            if (last == null || d.Title.Seerr is not { } seerr) return null;
            var later = all.Skip(all.IndexOf(last) + 1).FirstOrDefault(e => e.State != EpisodeState.Available);
            if (later == null) return null;
            return new HomeCard(CardKind.Series, $"tmdb:{seerr.MediaType}:{seerr.TmdbId}", d.Title.Name, $"Next: {later.Code}", d.Title.ImageUrl, null,
                later.State == EpisodeState.Requested ? "requested" : "get", "Seerr", PlayVia.None, Synopsis: d.Title.Synopsis);
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
        private async Task<ShowDetails> ComputeAsync(CatalogueTitle title, Action<ShowDetails>? progress, bool fresh, CancellationToken ct, Outcome? outcome = null)
        {
            var t = title;
            var searching = t.Sources.Count == 0 && t.Kind != TitleKind.Episode;
            var sources = searching ? _search.SearchSources(t.Name, ct) : Array.Empty<(string Source, Task<IReadOnlyList<MediaItemViewModel>?> Results)>();

            if (t.Seerr == null) t = t with { Seerr = await _search.FindInSeerrAsync(t, ct).ConfigureAwait(false) };
            var status = await StatusAsync(t, fresh, ct, outcome).ConfigureAwait(false);
            if (status != null && t.Seerr != null) t = t with { Seerr = t.Seerr with { Status = status.Status } };
            // A card from Downloads or a search hit may carry no description: Seerr (TMDB) has one.
            if (status?.Overview is { } overview && string.IsNullOrWhiteSpace(t.Synopsis)) t = t with { Synopsis = overview };

            var remaining = sources.Select(s => s.Results).ToList();
            var found = new List<MediaItemViewModel>();
            var seasonDetails = new Dictionary<int, JellyseerrSeasonDetails>();
            var details = await ComposeAsync(t, status, remaining.Count > 0, seasonDetails, progress, fresh, ct, outcome).ConfigureAwait(false);
            progress?.Invoke(details);
            while (remaining.Count > 0)
            {
                var done = await Task.WhenAny(remaining).ConfigureAwait(false);
                remaining.Remove(done);
                ct.ThrowIfCancellationRequested();
                if (done.IsCompletedSuccessfully && done.Result is { } items) found.AddRange(items);
                else outcome?.Fail(); // a source that didn't answer may have had it
                var match = CatalogueSearch.Group(found, Array.Empty<JellyseerrSearchResult>()).Titles.FirstOrDefault(x => CatalogueSearch.Matches(x, t));
                var changed = match != null && !match.Sources.Select(s => s.ItemId).SequenceEqual(t.Sources.Select(s => s.ItemId));
                if (changed) t = t with { Sources = match!.Sources, ImageUrl = t.ImageUrl ?? match.ImageUrl, Synopsis = t.Synopsis ?? match.Synopsis, Year = t.Year ?? match.Year };
                if (!changed && remaining.Count > 0) continue;
                details = changed ? await ComposeAsync(t, status, remaining.Count > 0, seasonDetails, progress, fresh, ct, outcome).ConfigureAwait(false) : details with { CheckingLibrary = false };
                progress?.Invoke(details);
            }
            return details;
        }

        private async Task<ShowDetails> ComposeAsync(CatalogueTitle t, JellyseerrMediaStatus? status, bool checking, Dictionary<int, JellyseerrSeasonDetails> seasonDetails, Action<ShowDetails>? early, bool fresh, CancellationToken ct, Outcome? outcome)
        {
            if (t.Kind != TitleKind.Series)
            {
                var film = t.Sources.Count > 0 || status?.Status == 5 ? SeasonState.Available
                    : status?.Status is 2 or 3 or 4 ? SeasonState.Requested : SeasonState.Missing;
                return new ShowDetails(t, Array.Empty<Season>(), film, status?.Progress(), checking);
            }
            var library = await LibrarySeasonsAsync(t, ct, fresh, outcome).ConfigureAwait(false);
            var details = new ShowDetails(t, Merge(library, status, t.Best?.Service, seasonDetails), CheckingLibrary: checking);
            // Seerr names the episodes the library lacks: ask for the season the page opens on (bounded, so a hung Seerr can't stall it).
            var i = Prefetcher.OpeningSeason(details);
            if (i < 0 || _seerr == null || t.Seerr == null) return details;
            var number = details.Seasons[i].Number;
            if (!seasonDetails.TryGetValue(number, out var season))
            {
                early?.Invoke(details); // the page paints with placeholder names now; the real ones follow if Seerr answers
                var seerr = _seerr; var r = t.Seerr;
                var answer = await Deadline.Within<(bool Ok, JellyseerrSeasonDetails? Value)>(async c => (true, await seerr.GetSeasonAsync(r.TmdbId, number, c).ConfigureAwait(false)),
                    (false, null), _pieceTimeout, $"[ShowData] Seerr season {number}", ct).ConfigureAwait(false);
                if (!answer.Ok) outcome?.Fail();
                season = answer.Value;
                if (season != null) seasonDetails[number] = season;
            }
            return season == null ? details : details with { Seasons = Merge(library, status, t.Best?.Service, seasonDetails) };
        }

        private async Task<JellyseerrMediaStatus?> StatusAsync(CatalogueTitle t, bool fresh, CancellationToken ct, Outcome? outcome)
        {
            if (_seerr == null || t.Seerr == null) return null;
            var seerr = _seerr; var r = t.Seerr;
            // (true, answer) when Seerr answered (even "unknown to me"); the (false, null) fallback marks a timeout or failure, which isn't kept.
            return await CachedAsync(_statusCache, $"{r.MediaType}:{r.TmdbId}", fresh,
                c => Deadline.Within<(bool Ok, JellyseerrMediaStatus? Value)>(async c2 => (true, await seerr.GetMediaStatusAsync(r.TmdbId, r.MediaType, c2).ConfigureAwait(false)), (false, null), _pieceTimeout, "[ShowData] Seerr status", c), ct, outcome).ConfigureAwait(false);
        }

        public sealed record LibrarySeason(int Number, string Label, IReadOnlyList<MediaItemViewModel> Episodes, string? Service = null, string? MoreId = null);

        /// <summary>
        /// The show's seasons in the library, across its sources (the best source wins a season both have): each source's
        /// season folders, or one season when it lists only episodes.
        /// </summary>
        public Task<IReadOnlyList<LibrarySeason>> LibrarySeasonsAsync(CatalogueTitle t, CancellationToken ct = default, bool fresh = false) => LibrarySeasonsAsync(t, ct, fresh, null);

        private async Task<IReadOnlyList<LibrarySeason>> LibrarySeasonsAsync(CatalogueTitle t, CancellationToken ct, bool fresh, Outcome? outcome)
        {
            var perSource = await Task.WhenAll(t.Sources.Select(src => CachedAsync(_seasonCache, $"{src.Service}|{src.ItemId}", fresh, c => SeasonsFromAsync(t, src, c), ct, outcome))).ConfigureAwait(false);
            var seasons = new List<LibrarySeason>();
            foreach (var list in perSource)
                foreach (var s in list)
                    if (seasons.All(x => x.Number != s.Number)) seasons.Add(s);
            return seasons.OrderBy(s => s.Number == 0 ? int.MaxValue : s.Number).ToList();
        }

        /// <summary>A provider's children, or null when it doesn't answer in time (or fails): not the same as answering "none".</summary>
        private Task<IEnumerable<MediaItemViewModel>?> Children(string id, string what, CancellationToken ct) =>
            Deadline.Within<IEnumerable<MediaItemViewModel>?>(async _ => await _data.GetChildrenAsync(id).ConfigureAwait(false), null, _pieceTimeout, $"[ShowData] Listing {what}", ct);

        /// <summary>
        /// One source's seasons. Ok is false when any listing timed out or failed (what's returned is then what could be learnt, and
        /// isn't worth keeping); a provider that answered with nothing is Ok.
        /// </summary>
        private async Task<(bool Ok, IReadOnlyList<LibrarySeason> Value)> SeasonsFromAsync(CatalogueTitle t, TitleSource src, CancellationToken ct)
        {
            var ok = true;
            try
            {
                var listed = await Children(src.ItemId, $"{t.Name} from {src.Service}", ct).ConfigureAwait(false);
                if (listed == null) return (false, Array.Empty<LibrarySeason>());
                var children = listed.ToList();
                if (children.Count == 0) return (true, Array.Empty<LibrarySeason>());
                var folders = children.Where(c => c.Type == Baird.Services.MediaType.Brand).ToList();
                if (folders.Count == 0) return (true, new[] { new LibrarySeason(1, "Season 1", children, src.Service, YouTubeService.NextPageId(src.ItemId, children.Count)) });
                var seasons = await Task.WhenAll(folders.Select(async (f, i) =>
                {
                    var inFolder = await Children(f.Id, $"{t.Name} {f.Name} from {src.Service}", ct).ConfigureAwait(false);
                    if (inFolder == null) ok = false;
                    var eps = (inFolder ?? Enumerable.Empty<MediaItemViewModel>()).Where(e => e.Type != Baird.Services.MediaType.Brand).ToList();
                    // A YouTube channel's seasons are its playlists, which have names rather than numbers.
                    if (f.Source == YouTubeService.SourceName) return new LibrarySeason(i + 1, f.Name, eps, src.Service, YouTubeService.NextPageId(f.Id, eps.Count));
                    var n = SeasonNumberOf(f.Name, i + 1);
                    return new LibrarySeason(n, n == 0 ? "Specials" : $"Season {n}", eps, src.Service);
                })).ConfigureAwait(false);
                return (ok, seasons);
            }
            catch (Exception ex) { Console.WriteLine($"[ShowData] Seasons of {t.Name} from {src.Service} failed: {ex.Message}"); return (false, Array.Empty<LibrarySeason>()); }
        }

        /// <summary>
        /// The next page of a season that lists its episodes a page at a time (<see cref="Season.MoreId"/>): the new episodes,
        /// numbered on from the season's last, and the id of the page after (null at the end).
        /// </summary>
        public async Task<(IReadOnlyList<Episode> Episodes, string? MoreId)> MoreEpisodesAsync(Season season, CancellationToken ct = default)
        {
            if (season.MoreId == null) return (Array.Empty<Episode>(), null);
            var page = (await _data.GetChildrenAsync(season.MoreId).ConfigureAwait(false)).Where(e => e.Type != Baird.Services.MediaType.Brand).ToList();
            ct.ThrowIfCancellationRequested();
            var known = season.Episodes.Select(e => e.Source?.ItemId).ToHashSet();
            var fresh = page.Where(m => !known.Contains(m.Id)).ToList();
            var episodes = fresh.Select((m, i) => EpisodeOf(m, season.Number, season.Episodes.Count + i + 1)).ToList();
            return (episodes, fresh.Count == 0 ? null : YouTubeService.NextPageId(season.MoreId, page.Count));
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
        public static IReadOnlyList<Season> Merge(IReadOnlyList<LibrarySeason> library, JellyseerrMediaStatus? status, string? service, IReadOnlyDictionary<int, JellyseerrSeasonDetails>? seasonDetails = null)
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
                var named = seasonDetails != null && seasonDetails.TryGetValue(n, out var sd) ? sd.Episodes : null;
                for (var e = eps.Count + 1; e <= count; e++)
                {
                    var info = named?.FirstOrDefault(x => x.Number == e);
                    var name = string.IsNullOrWhiteSpace(info?.Name) ? $"Episode {e}" : info!.Name;
                    eps.Add(new Episode(n, e, $"{e}. {name}", null, info?.StillUrl, Pending(e), Download: Dl(e), Synopsis: info?.Overview));
                }

                var label = lib?.Label ?? (n == 0 ? "Specials" : $"Season {n}");
                if (lib != null && lib.Episodes.Count > 0)
                {
                    var partial = count > lib.Episodes.Count;
                    seasons.Add(new Season(n, label, partial ? SeasonState.Partial : SeasonState.Available, lib.Service ?? service, eps,
                        count > 0 ? count : lib.Episodes.Count, lib.Episodes.Count, partial && onTheWay ? status?.Progress(n) : null, lib.MoreId));
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
                _search.ForgetDownloads(); // Search's Downloads list was read before this request: read it again
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
