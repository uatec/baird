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
        /// <summary>The sources a search of one lane's providers found for a title (key: title|lane): so a page needn't search, and shows what was found at once.</summary>
        private readonly StaleCache<IReadOnlyList<TitleSource>> _foundCache;
        /// <summary>One season's listing from a source that lists a season at a time (key: source|season folder), served stale and refreshed.</summary>
        private readonly StaleCache<LibrarySeason> _episodesCache;
        /// <summary>Each such source's season folders (key: source), so one season can be listed on its own.</summary>
        private readonly Dictionary<string, IReadOnlyList<SeasonFolder>> _folders = new();
        /// <summary>The season each title's page last opened on: the one listed first.</summary>
        private readonly Dictionary<string, int> _opening = new();
        private readonly Dictionary<string, DateTimeOffset> _refreshing = new(); // key -> when its refresh was requested
        private readonly Func<DateTimeOffset> _clock;
        /// <summary>A refresh asked for this long ago and never finished is taken to be lost (a sink that dropped it), and may be asked for again.</summary>
        private static readonly TimeSpan RefreshLost = TimeSpan.FromMinutes(2);

        /// <summary>
        /// When set, a stale answer's background refresh is handed here (key, work) instead of being run by ShowData itself.
        /// The sink MUST run the work (it never faults); one that drops it only delays that key's next refresh by two minutes.
        /// </summary>
        public Action<string, Func<CancellationToken, Task>>? RefreshSink { get; set; }

        /// <summary>
        /// When set, the seasons of a show that were not listed with the first (a service that drives the browser lists the season in view and
        /// leaves the rest for later) are handed here as one batch (key, work), nearest the opening season first, to run one at a time at low
        /// priority. Without a sink they stay "–" until <see cref="EnsureSeasonListedAsync"/> is asked for one.
        /// </summary>
        public Action<IReadOnlyList<(string Key, Func<CancellationToken, Task> Work)>>? BackgroundSink { get; set; }

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
            _foundCache = new(AvailabilityFresh, now);
            _episodesCache = new(AvailabilityFresh, now);
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
            public readonly Outcome Outcome = new();      // some piece failed: its answers are no better than the kept record
            public DateTimeOffset At = DateTimeOffset.UtcNow;
        }

        /// <summary>What a warm or background refresh learnt about a title a page may be showing.</summary>
        public enum ShowChange
        {
            /// <summary>Availability (Seerr status, a source's seasons) was refreshed: ask again (the shared answer was dropped).</summary>
            Availability,
            /// <summary>Episode names were kept for a season that had only placeholders: re-fill from the store (<see cref="WithKeptNames"/>), no network.</summary>
            Names,
        }

        /// <summary>
        /// Raised (on any thread) only by background writes: a stale answer's refresh completing, or <see cref="EnsureSeasonAsync"/>
        /// keeping names. Never by a page's own <see cref="DetailsAsync"/>, so a page that re-asks on it never loops.
        /// </summary>
        public event Action<string, ShowChange>? Changed;

        private void RaiseChanged(string key, ShowChange change)
        {
            // Availability: the shared answer was worked out from what is now out of date, so the next ask starts again (a page joined
            // to a compute in flight keeps it; only the next ask is affected).
            if (change == ShowChange.Availability) lock (_cache) _cache.Remove(key);
            try { Changed?.Invoke(key, change); }
            catch (Exception ex) { Console.WriteLine($"[ShowData] Telling pages {key} changed failed: {ex.Message}"); }
        }

        /// <summary>The details with any placeholder episode ("3. Episode 3") filled from the kept record (names kept since they were learnt). No network.</summary>
        public ShowDetails WithKeptNames(ShowDetails d) => Kept(d.Title) is { } s ? Fill(d, s) : d;

        /// <summary>
        /// How long one title's kept metadata is fresh for the warming: <see cref="MetadataFresh"/> moved by up to a day either way, by a
        /// hash of its key that is the same every run, so records kept together don't all go stale (and get refreshed) together.
        /// </summary>
        public static TimeSpan FreshFor(string key)
        {
            uint h = 2166136261; // FNV-1a: stable across runs (string.GetHashCode is not)
            foreach (var c in key) { h ^= c; h *= 16777619; }
            var spread = h / (double)uint.MaxValue * 2 - 1; // -1 .. 1
            return MetadataFresh + TimeSpan.FromDays(spread);
        }

        private bool IsFresh(StoredShow s, string key) => _clock() - s.Stamp < FreshFor(key);
        private readonly Dictionary<string, Entry> _cache = new();
        public static readonly TimeSpan Keep = TimeSpan.FromSeconds(90);

        /// <summary>Drop what's been learnt (after a request, or watching something).</summary>
        public void Forget()
        {
            _statusCache.Clear(); _seasonCache.Clear(); _foundCache.Clear(); _episodesCache.Clear();
            lock (_cache) foreach (var k in _cache.Where(e => e.Value.Task.IsCompleted).Select(e => e.Key).ToList()) _cache.Remove(k);
        }

        /// <summary>
        /// Answer from the availability cache: a fresh hit as is; a stale hit at once, with a refresh started in the background;
        /// otherwise ask (a failed answer is told to <paramref name="outcome"/>). <paramref name="fetch"/> says whether it really answered (a timeout or failure is <c>Ok = false</c> with the
        /// fallback): only a real answer is kept, so a bad moment is never served back for ever. <paramref name="fresh"/> skips the cache.
        /// </summary>
        private async Task<T> CachedAsync<T>(StaleCache<T> cache, string key, bool fresh, Func<CancellationToken, Task<(bool Ok, T Value)>> fetch, CancellationToken ct, Outcome? outcome = null, string? owner = null)
        {
            if (!fresh && cache.TryGet(key, out var kept, out var stale))
            {
                // Refreshed: the page showing the title it was served for is told, so the refreshed answer reaches the screen.
                if (stale) Refresh(key, async c =>
                {
                    var r = await fetch(c).ConfigureAwait(false);
                    if (!r.Ok) return;
                    cache.Put(key, r.Value);
                    if (owner != null) RaiseChanged(owner, ShowChange.Availability);
                });
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
        public async Task<ShowDetails> DetailsAsync(CatalogueTitle title, Action<ShowDetails>? progress = null, CancellationToken ct = default, bool fresh = false) =>
            (await DetailsCoreAsync(title, progress, ct, fresh).ConfigureAwait(false)).Details;

        /// <summary><see cref="DetailsAsync"/>, and whether every piece answered (false: a timeout, a failure, or the budget ran out).</summary>
        private async Task<(ShowDetails Details, bool Sure)> DetailsCoreAsync(CatalogueTitle title, Action<ShowDetails>? progress, CancellationToken ct, bool fresh)
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
                    try
                    {
                        var d = await waited.ConfigureAwait(false);
                        // Names Seerr gave a season since (the warming) fill its placeholders: a page reopened shortly after still has them.
                        return (Kept(title) is { } now ? Fill(d, now) : d, !e.Outcome.Failed);
                    }
                    catch (Exception ex) when (stored != null && !ct.IsCancellationRequested && Known() is { } kept)
                    {
                        Console.WriteLine($"[ShowData] {title.Name}: {ex.Message} - showing what was kept");
                        return (kept, false);
                    }
                }
                if (Known() is { } known) return (known, false);
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
            if (stored != null && IsFresh(stored, title.Key)) return false;
            Entry e;
            lock (_cache)
            {
                // An answer already worked out is no refresh: ask again unless one is in flight. Asked afresh: never from the availability
                // cache (a stale answer is never served, so no refresh job is queued ahead of the page being warmed).
                e = Join(title, true, stored, renew: _cache.TryGetValue(title.Key, out var cur) && cur.Task.IsCompleted);
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
        /// For the warming queue, a title that carries no source (a Seerr card): when its kept record is missing or stale
        /// (<see cref="FreshFor"/>), learn what Seerr alone knows (its status and the opening season's names) and keep that. Never searches
        /// the providers (YouTube's search is yt-dlp), never lists a library, and never shares the work with a page (a page opened meanwhile
        /// does its own full compute). Nothing is kept when Seerr fails or answers nothing, or when its answer has fewer seasons than the
        /// record. Returns whether a record was kept. Never throws but for cancellation of <paramref name="ct"/>.
        /// </summary>
        public async Task<bool> EnsureFreshFromSeerrAsync(CatalogueTitle title, CancellationToken ct = default)
        {
            if (_store == null || _seerr == null || title.Sources.Count > 0) return false;
            var stored = Kept(title);
            if (stored != null && IsFresh(stored, title.Key)) return false;
            if ((title.Seerr ?? stored?.Title.Seerr) is not { } r) return false;
            try
            {
                var outcome = new Outcome();
                var t = title with { Seerr = r };
                var status = await StatusAsync(t, true, ct, outcome).ConfigureAwait(false); // asked afresh: no stale answer, no refresh queued
                if (status == null || outcome.Failed) return false;
                t = t with { Seerr = r with { Status = status.Status } };
                if (status.Overview is { } overview && string.IsNullOrWhiteSpace(t.Synopsis)) t = t with { Synopsis = overview };
                var d = Fill(await ComposeAsync(t, status, false, new Dictionary<int, JellyseerrSeasonDetails>(), null, true, ct, outcome).ConfigureAwait(false), stored);
                if (outcome.Failed || ct.IsCancellationRequested || (stored != null && d.Seasons.Count < stored.Seasons.Count)) return false;
                _store.Put(title.Key, ToStored(d, _clock()));
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[ShowData] Warming {title.Name} from Seerr failed: {ex.Message}");
                return false;
            }
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
            catch (Exception ex) { Console.WriteLine($"[ShowData] Keeping {title.Name} season {seasonNumber} failed: {ex.Message}"); return Stills(filled); }
            RaiseChanged(title.Key, ShowChange.Names); // a page showing the placeholders fills them in
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
            // A compute that failed somewhere is not shared once done (as a failed availability answer isn't cached): the next ask tries again.
            if (!renew && _cache.TryGetValue(title.Key, out var e) && !e.Task.IsCanceled && !e.Task.IsFaulted && !(e.Task.IsCompleted && e.Outcome.Failed)) return e;
            var entry = new Entry();
            void Report(ShowDetails d)
            {
                d = Fill(d, stored);
                Action<ShowDetails, long>? listeners;
                long seq;
                lock (_cache)
                {
                    // With a kept record, nothing learnt after a piece failed is shown: it could only be worse (a provider that is down
                    // answers "nothing"), and the page keeps what it has.
                    if (stored != null && entry.Outcome.Failed) return;
                    entry.Latest = d; seq = ++entry.Seq; listeners = entry.Progress;
                }
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

        /// <summary>
        /// Work out the details live and, when every piece answered, keep them (metadata only) for next time. With a kept record, a
        /// compute where some piece failed (timed out, threw, or answered nothing where something must be: see <see cref="StatusAsync"/>
        /// and <see cref="SeasonsFromAsync"/>) is never kept and never replaces what the page has: it returns the last live report made
        /// before the failure, else the record unchanged (still checking, "–"). A complete answer with fewer seasons than the record is
        /// not trusted either: the record, unchanged. With no record, what was learnt is returned as it always was (and not kept).
        /// </summary>
        private async Task<ShowDetails> RunAsync(CatalogueTitle title, Action<ShowDetails> report, bool fresh, StoredShow? stored, Entry entry)
        {
            var outcome = entry.Outcome;
            var d = Fill(await ComputeAsync(title, report, fresh, entry.Cts.Token, outcome).ConfigureAwait(false), stored);
            if (Warmer.OpeningSeason(d) is >= 0 and var open && !d.Seasons[open].Pending) lock (_opening) _opening[title.Key] = d.Seasons[open].Number; // the season to list first next time
            if (stored != null && outcome.Failed)
            {
                Console.WriteLine($"[ShowData] {title.Name}: something didn't answer - keeping what was kept");
                lock (_cache) return entry.Seq > 0 ? entry.Latest! : FromStore(title, stored);
            }
            if (stored != null && d.Seasons.Count < stored.Seasons.Count)
            {
                Console.WriteLine($"[ShowData] {title.Name}: {d.Seasons.Count} seasons where {stored.Seasons.Count} were kept - keeping the record");
                outcome.Fail(); // not shared: the next ask tries again
                return FromStore(title, stored);
            }
            if (_store != null && !outcome.Failed && !d.CheckingLibrary && !entry.Cts.IsCancellationRequested)
            {
                try { _store.Put(title.Key, ToStored(d, _clock()) with { Opening = OpeningOf(title) ?? stored?.Opening }); entry.Stored = true; }
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
                    if (k == null || Placeholder(k)) return e; // nothing more kept for it either
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
            var (d, sure) = await DetailsCoreAsync(series, null, ct, false).ConfigureAwait(false);
            // Only a kept record, unverified, or something didn't answer (Jellyfin not up yet lists nothing, so every episode would look
            // missing and be offered to "get"): nothing to offer on it.
            if (d.CheckingLibrary || !sure) return null;
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
        // ------------------------------------------------------------------ availability warming, in two lanes

        /// <summary>
        /// A source that is costly to ask in the background: YouTube (every listing is a yt-dlp process) or a service played in the
        /// browser (its listings drive the one shared browser). They are warmed in their own lane, one at a time, so they never hold up
        /// the rest.
        /// </summary>
        public static bool IsCostly(string service) => service == YouTubeService.SourceName || HomeData.ViaOf(service) == PlayVia.Browser;

        /// <summary>Whether a refresh key (a source's seasons "service|id", or a provider's answer for a title "found:title|provider") belongs to the costly lane.</summary>
        public static bool IsCostlyKey(string key) =>
            key.StartsWith("found:", StringComparison.Ordinal) ? IsCostly(key[(key.LastIndexOf('|') + 1)..]) : IsCostly(key.Split('|')[0]);

        /// <summary>Cheap: plain HTTP providers. Costly: the services played in the browser (warmed one job at a time). YouTube: every request is a yt-dlp process: never warmed, only asked when a page opens.</summary>
        public enum WarmLane { Cheap, Costly, YouTube }

        public static WarmLane LaneOf(string service) =>
            service == YouTubeService.SourceName ? WarmLane.YouTube : HomeData.ViaOf(service) == PlayVia.Browser ? WarmLane.Costly : WarmLane.Cheap;

        /// <summary>How old a provider's answer about a title may be before the warming asks that provider again.</summary>
        public static readonly TimeSpan WarmFoundFor = TimeSpan.FromMinutes(10);

        private static string FoundKey(CatalogueTitle t, string provider) => $"found:{t.Key}|{provider}";

        /// <summary>
        /// What one provider says of a title: its sources for it (none when it hasn't got it), kept per provider (served stale for ever,
        /// refreshed when asked for again). Not answered (null) when it timed out or failed: nothing is kept, so one flaky provider
        /// never costs the others' answers.
        /// </summary>
        private async Task<IReadOnlyList<TitleSource>?> AskProviderAsync(CatalogueTitle t, string provider, CancellationToken ct)
        {
            var asked = _search.SearchSources(t.Name, ct, name => name == provider);
            if (asked.Count == 0) return Array.Empty<TitleSource>();
            await asked[0].Results.ConfigureAwait(false);
            if (!(asked[0].Results.IsCompletedSuccessfully && asked[0].Results.Result is { } items)) return null;
            var found = SourcesFrom(items, t);
            _foundCache.Put(FoundKey(t, provider), found);
            return found;
        }

        private static IReadOnlyList<TitleSource> SourcesFrom(IEnumerable<MediaItemViewModel> items, CatalogueTitle t) =>
            CatalogueSearch.Group(items.ToList(), Array.Empty<JellyseerrSearchResult>()).Titles.FirstOrDefault(x => CatalogueSearch.Matches(x, t))?.Sources
            ?? (IReadOnlyList<TitleSource>)Array.Empty<TitleSource>();

        /// <summary>
        /// The sources the providers of one lane have for a title: each provider's answer is reused while fresh and asked for when it isn't
        /// (a stale one stands in for a provider that doesn't answer now). Complete is false when some provider answered nothing at all,
        /// stale or fresh.
        /// </summary>
        private async Task<(IReadOnlyList<TitleSource> Sources, bool Complete)> FoundAsync(CatalogueTitle t, WarmLane lane, CancellationToken ct)
        {
            var found = new List<TitleSource>();
            var ask = new List<(string Provider, IReadOnlyList<TitleSource>? Old)>();
            foreach (var name in _data.Providers.Select(p => p.Name).Where(n => LaneOf(n) == lane))
            {
                // Which services carry a title changes rarely: the warming asks again only when the answer is older than WarmFoundFor
                // (a page shows a stale one at once and refreshes it), so it doesn't spend its time re-asking the browser what it just said.
                if (_foundCache.TryGetAge(FoundKey(t, name), out var kept, out var age))
                {
                    if (age < WarmFoundFor) found.AddRange(kept); else ask.Add((name, kept));
                }
                else ask.Add((name, null));
            }
            var answers = await Task.WhenAll(ask.Select(a => AskProviderAsync(t, a.Provider, ct))).ConfigureAwait(false);
            var complete = true;
            for (var i = 0; i < ask.Count; i++)
            {
                if (answers[i] is { } now) found.AddRange(now);
                else if (ask[i].Old is { } old) found.AddRange(old);
                else complete = false;
            }
            return (found, complete);
        }

        /// <summary>
        /// For the warming queue: learns what one lane can say of where a title can be watched, and keeps it in the availability caches (60 s
        /// fresh, then served stale while refreshed): Seerr's status (cheap lane), the seasons of the title's own sources in the lane, and, for
        /// a title with no source, the sources a search of the lane's providers finds (and their seasons). Only what isn't fresh already is
        /// asked, so a title warmed a moment ago costs nothing. Returns whether everything asked answered. Never throws but for
        /// cancellation of <paramref name="ct"/>.
        /// </summary>
        public async Task<bool> WarmAvailabilityAsync(CatalogueTitle title, WarmLane lane, CancellationToken ct = default)
        {
            if (title.Kind == TitleKind.Episode) return true;
            try
            {
                var outcome = new Outcome();
                var t = title;
                if (lane == WarmLane.Cheap && _seerr != null)
                {
                    if (t.Seerr == null) t = t with { Seerr = await _search.FindInSeerrAsync(t, ct).ConfigureAwait(false) };
                    if (t.Seerr is { } r && !(_statusCache.TryGet($"{r.MediaType}:{r.TmdbId}", out _, out var statusStale) && !statusStale))
                        await StatusAsync(t, true, ct, outcome).ConfigureAwait(false);
                }
                IReadOnlyList<TitleSource> own = t.Sources.Where(s => LaneOf(s.Service) == lane).ToList();
                if (t.Sources.Count == 0 && t.Kind != TitleKind.Episode)
                {
                    var (sources, complete) = await FoundAsync(t, lane, ct).ConfigureAwait(false);
                    own = sources;
                    if (!complete) outcome.Fail();
                }
                if (t.Kind == TitleKind.Series && own.Count > 0)
                {
                    var fresh = own.Any(s => !(_seasonCache.TryGetAge(KeyOf(s), out _, out var age) && age < WarmFoundFor));
                    if (fresh) await LibrarySeasonsAsync(t with { Sources = own }, ct, true, outcome).ConfigureAwait(false);
                }
                return !outcome.Failed;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[ShowData] Warming availability of {title.Name} ({lane}) failed: {ex.Message}");
                return false;
            }
        }

        private async Task<ShowDetails> ComputeAsync(CatalogueTitle title, Action<ShowDetails>? progress, bool fresh, CancellationToken ct, Outcome? outcome = null)
        {
            var t = title;
            var searching = t.Sources.Count == 0 && t.Kind != TitleKind.Episode;
            // What the warming (or an earlier page) found is used at once, stale or not (a stale answer is refreshed in the background);
            // only a lane never searched is searched now, so a page doesn't wait on the browser's providers when they were asked already.
            var missing = new List<string>();
            if (searching)
            {
                var kept = new List<TitleSource>();
                foreach (var name in _data.Providers.Select(p => p.Name))
                {
                    var key = FoundKey(t, name);
                    if (!fresh && _foundCache.TryGet(key, out var f, out var stale))
                    {
                        kept.AddRange(f);
                        if (stale)
                        {
                            var owner = t; var provider = name;
                            Refresh(key, async c => { if (await AskProviderAsync(owner, provider, c).ConfigureAwait(false) is not null) RaiseChanged(owner.Key, ShowChange.Availability); });
                        }
                    }
                    else missing.Add(name);
                }
                if (kept.Count > 0) t = t with { Sources = kept.DistinctBy(x => x.ItemId).ToList() };
            }
            var sources = missing.Count > 0
                ? _search.SearchSources(t.Name, ct, missing.Contains)
                : Array.Empty<(string Source, Task<IReadOnlyList<MediaItemViewModel>?> Results)>();

            // The library's seasons and Seerr's view are asked at once: a slow Seerr never holds back what the library has (Play).
            var library = t.Kind == TitleKind.Series && t.Sources.Count > 0 ? LibrarySeasonsAsync(t, ct, fresh, outcome) : null;
            var seerrView = SeerrViewAsync(t, fresh, ct, outcome);
            if (library != null && progress != null)
            {
                await Task.WhenAny(library, seerrView).ConfigureAwait(false);
                if (!seerrView.IsCompleted && library.IsCompletedSuccessfully && library.Result.Count > 0)
                    progress(new ShowDetails(t, Merge(library.Result, null, t.Best?.Service), CheckingLibrary: false)); // Seerr fills in the rest when it answers
            }
            var (seen, status) = await seerrView.ConfigureAwait(false);
            t = seen;

            var searched = sources;
            var remaining = sources.Select(s => s.Results).ToList();
            var found = new List<MediaItemViewModel>();
            var seasonDetails = new Dictionary<int, JellyseerrSeasonDetails>();
            var details = await ComposeAsync(t, status, remaining.Count > 0, seasonDetails, progress, fresh, ct, outcome, library).ConfigureAwait(false);
            progress?.Invoke(details);
            while (remaining.Count > 0)
            {
                var done = await Task.WhenAny(remaining).ConfigureAwait(false);
                remaining.Remove(done);
                ct.ThrowIfCancellationRequested();
                if (done.IsCompletedSuccessfully && done.Result is { } items) found.AddRange(items);
                else outcome?.Fail(); // a source that didn't answer may have had it
                var match = CatalogueSearch.Group(found, Array.Empty<JellyseerrSearchResult>()).Titles.FirstOrDefault(x => CatalogueSearch.Matches(x, t));
                var changed = match != null && match.Sources.Any(s => t.Sources.All(x => x.ItemId != s.ItemId));
                if (changed) t = t with { Sources = t.Sources.Concat(match!.Sources.Where(s => t.Sources.All(x => x.ItemId != s.ItemId))).ToList(), ImageUrl = t.ImageUrl ?? match.ImageUrl, Synopsis = t.Synopsis ?? match.Synopsis, Year = t.Year ?? match.Year };
                if (!changed && remaining.Count > 0) continue;
                details = changed ? await ComposeAsync(t, status, remaining.Count > 0, seasonDetails, progress, fresh, ct, outcome).ConfigureAwait(false) : details with { CheckingLibrary = false };
                progress?.Invoke(details);
            }
            // Each provider that answered is kept for the next page (and the warming), where "none" is an answer too.
            foreach (var x in searched)
                if (x.Results.IsCompletedSuccessfully && x.Results.Result is { } answered)
                    _foundCache.Put(FoundKey(t, x.Source), SourcesFrom(answered, t));
            return details;
        }

        /// <summary>The title with what Seerr says of it (its entry looked up when the title doesn't carry one, its status, its synopsis), and the status.</summary>
        private async Task<(CatalogueTitle Title, JellyseerrMediaStatus? Status)> SeerrViewAsync(CatalogueTitle t, bool fresh, CancellationToken ct, Outcome? outcome)
        {
            if (t.Seerr == null) t = t with { Seerr = await _search.FindInSeerrAsync(t, ct).ConfigureAwait(false) };
            var status = await StatusAsync(t, fresh, ct, outcome).ConfigureAwait(false);
            if (status != null && t.Seerr != null) t = t with { Seerr = t.Seerr with { Status = status.Status } };
            // A card from Downloads or a search hit may carry no description: Seerr (TMDB) has one.
            if (status?.Overview is { } overview && string.IsNullOrWhiteSpace(t.Synopsis)) t = t with { Synopsis = overview };
            return (t, status);
        }

        private async Task<ShowDetails> ComposeAsync(CatalogueTitle t, JellyseerrMediaStatus? status, bool checking, Dictionary<int, JellyseerrSeasonDetails> seasonDetails, Action<ShowDetails>? early, bool fresh, CancellationToken ct, Outcome? outcome,
            Task<IReadOnlyList<LibrarySeason>>? asked = null)
        {
            if (t.Kind != TitleKind.Series)
            {
                var film = t.Sources.Count > 0 || status?.Status == 5 ? SeasonState.Available
                    : status?.Status is 2 or 3 or 4 ? SeasonState.Requested : SeasonState.Missing;
                return new ShowDetails(t, Array.Empty<Season>(), film, status?.Progress(), checking);
            }
            var library = asked != null ? await asked.ConfigureAwait(false) : await LibrarySeasonsAsync(t, ct, fresh, outcome).ConfigureAwait(false);
            var details = new ShowDetails(t, Merge(library, status, t.Best?.Service, seasonDetails), CheckingLibrary: checking);
            // Seerr names the episodes the library lacks: ask for the season the page opens on (bounded, so a hung Seerr can't stall it).
            var i = Warmer.OpeningSeason(details);
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
            // Seerr always has a status for a title it has an entry for (a title nobody asked for is "unknown", status 1): no answer (null) is
            // how the service reports any failure (it swallows them), so it counts as one, like a timeout. Neither is kept.
            return await CachedAsync(_statusCache, $"{r.MediaType}:{r.TmdbId}", fresh,
                c => Deadline.Within<(bool Ok, JellyseerrMediaStatus? Value)>(async c2 => await seerr.GetMediaStatusAsync(r.TmdbId, r.MediaType, c2).ConfigureAwait(false) is { } s ? (true, s) : (false, null),
                    (false, null), _pieceTimeout, "[ShowData] Seerr status", c), ct, outcome, t.Key).ConfigureAwait(false);
        }

        /// <param name="Pending">The season's folder is known, its episodes are not listed yet (see <see cref="Season.Pending"/>).</param>
        public sealed record LibrarySeason(int Number, string Label, IReadOnlyList<MediaItemViewModel> Episodes, string? Service = null, string? MoreId = null, bool Pending = false);

        /// <summary>
        /// The show's seasons in the library, across its sources (the best source wins a season both have): each source's
        /// season folders, or one season when it lists only episodes.
        /// </summary>
        public Task<IReadOnlyList<LibrarySeason>> LibrarySeasonsAsync(CatalogueTitle t, CancellationToken ct = default, bool fresh = false) => LibrarySeasonsAsync(t, ct, fresh, null);

        private async Task<IReadOnlyList<LibrarySeason>> LibrarySeasonsAsync(CatalogueTitle t, CancellationToken ct, bool fresh, Outcome? outcome)
        {
            var asked = t.Sources.Select(_ => new Outcome()).ToArray();
            var perSource = await Task.WhenAll(t.Sources.Select((src, i) => CachedAsync(_seasonCache, KeyOf(src), fresh, async c =>
            {
                var r = await SeasonsFromAsync(t, src, c).ConfigureAwait(false);
                // "Nothing" again where nothing was kept (refreshing a source known to have nothing now) is an answer; where something was
                // kept, it never replaces it.
                return !r.Ok && ReferenceEquals(r.Value, NothingListed) && _seasonCache.TryGet(KeyOf(src), out var kept, out _) && kept.Count == 0
                    ? (true, (IReadOnlyList<LibrarySeason>)Array.Empty<LibrarySeason>()) : r;
            }, ct, asked[i], t.Key))).ConfigureAwait(false);
            // A source that timed out or failed fails the compute. One that listed nothing (how a provider that is down answers) does
            // only when no source listed anything: a show on two services may well have nothing on one of them right now (iPlayer), and
            // then that "nothing" is a real answer, kept like any other (a record is never replaced by fewer seasons in any case).
            var nothing = Enumerable.Range(0, perSource.Length).Where(i => ReferenceEquals(perSource[i], NothingListed)).ToList();
            if (Enumerable.Range(0, perSource.Length).Any(i => asked[i].Failed && !nothing.Contains(i))) outcome?.Fail();
            else if (nothing.Count > 0 && nothing.Count == perSource.Length) outcome?.Fail();
            else foreach (var i in nothing) _seasonCache.Put(KeyOf(t.Sources[i]), Array.Empty<LibrarySeason>());
            var seasons = new List<LibrarySeason>();
            foreach (var list in perSource)
                foreach (var s in list)
                {
                    var at = seasons.FindIndex(x => x.Number == s.Number);
                    if (at < 0) seasons.Add(s);
                    else if (seasons[at].Pending && !s.Pending) seasons[at] = s; // another source's real listing beats a "–"
                }
            return seasons.OrderBy(s => s.Number == 0 ? int.MaxValue : s.Number).ToList();
        }

        private static string KeyOf(TitleSource src) => $"{src.Service}|{src.ItemId}";

        /// <summary>What <see cref="SeasonsFromAsync"/> returns when the show's own listing was empty (told apart from a timeout by reference).</summary>
        private static readonly IReadOnlyList<LibrarySeason> NothingListed = new List<LibrarySeason>().AsReadOnly();

        /// <summary>A provider's children, or null when it doesn't answer in time (or fails): not the same as answering "none".</summary>
        /// <param name="show">The show being listed: its name goes on episodes that name it only by id.</param>
        private Task<IEnumerable<MediaItemViewModel>?> Children(string id, CatalogueTitle show, string what, CancellationToken ct) =>
            Deadline.Within<IEnumerable<MediaItemViewModel>?>(async _ => await _data.GetChildrenAsync(id, show.Name).ConfigureAwait(false), null, _pieceTimeout, $"[ShowData] Listing {what}", ct);

        /// <summary>
        /// One source's seasons. Ok is false when any listing timed out or failed (what's returned is then what could be learnt, and
        /// isn't worth keeping), and when the show's own listing is empty (<see cref="NothingListed"/>): the providers answer "nothing"
        /// for every failure (Jellyfin not signed in or restarting, an error swallowed on the way), so it may not be an answer at all.
        /// </summary>
        private async Task<(bool Ok, IReadOnlyList<LibrarySeason> Value)> SeasonsFromAsync(CatalogueTitle t, TitleSource src, CancellationToken ct)
        {
            var ok = true;
            try
            {
                var listed = await Children(src.ItemId, t, $"{t.Name} from {src.Service}", ct).ConfigureAwait(false);
                if (listed == null) return (false, Array.Empty<LibrarySeason>());
                var children = listed.ToList();
                if (children.Count == 0)
                {
                    Console.WriteLine($"[ShowData] {src.Service} listed nothing for {t.Name}");
                    return (false, NothingListed); // not kept as it stands: see LibrarySeasonsAsync
                }
                var folders = children.Where(c => c.Type == Baird.Services.MediaType.Brand).ToList();
                if (folders.Count == 0) return (true, new[] { new LibrarySeason(1, "Season 1", children, src.Service, YouTubeService.NextPageId(src.ItemId, children.Count)) });
                // A service that drives the browser (or yt-dlp) is asked for the season in view; the rest follow one at a time later.
                if (folders.Count > 1 && IsCostly(src.Service))
                    return await LazySeasonsAsync(t, src, folders.Select((f, i) => f.Source == YouTubeService.SourceName
                        ? new SeasonFolder(f.Id, i + 1, f.Name, true)
                        : SeasonFolderOf(f, i + 1)).ToList(), ct).ConfigureAwait(false);
                var seasons = await Task.WhenAll(folders.Select(async (f, i) =>
                {
                    var inFolder = await Children(f.Id, t, $"{t.Name} {f.Name} from {src.Service}", ct).ConfigureAwait(false);
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

        /// <summary>One season folder of a source: its id, the season number it stands for, and its name.</summary>
        private sealed record SeasonFolder(string Id, int Number, string Label, bool YouTube);

        private static SeasonFolder SeasonFolderOf(MediaItemViewModel f, int position)
        {
            var n = SeasonNumberOf(f.Name, position);
            return new SeasonFolder(f.Id, n, n == 0 ? "Specials" : $"Season {n}", false);
        }

        /// <summary>The season a title's page last opened on (the next episode's), from this run or the kept record; else null.</summary>
        private int? OpeningOf(CatalogueTitle t)
        {
            lock (_opening) if (_opening.TryGetValue(t.Key, out var n)) return n;
            return Kept(t)?.Opening;
        }

        /// <summary>
        /// A source that lists a season at a time: the season in view (the one last opened on, else the first) is listed now, and so is the
        /// next when the whole of that one is watched (that is where the next episode is); every other season is listed as far as it has been
        /// before (kept, served stale), else left <see cref="LibrarySeason.Pending"/> and listed later, one at a time, nearest the opening
        /// season first (<see cref="BackgroundSink"/>). So a page doesn't wait on 37 listings of a browser.
        /// </summary>
        private async Task<(bool Ok, IReadOnlyList<LibrarySeason> Value)> LazySeasonsAsync(CatalogueTitle t, TitleSource src, List<SeasonFolder> folders, CancellationToken ct)
        {
            var key = KeyOf(src);
            lock (_folders) _folders[key] = folders;
            var order = folders.OrderBy(f => f.Number == 0 ? int.MaxValue : f.Number).ToList();
            var remembered = OpeningOf(t);
            var active = order.FirstOrDefault(f => f.Number == remembered) ?? order[0];
            var now = new List<SeasonFolder> { active };
            var outcome = new Outcome();
            var listed = new Dictionary<int, LibrarySeason>();
            var first = await ListSeasonAsync(t, src, active, false, ct, outcome).ConfigureAwait(false);
            listed[active.Number] = first;
            // All of it watched: the next episode is in the season after it.
            var at = order.IndexOf(active);
            if (first.Episodes.Count > 0 && first.Episodes[^1].History is { IsFinished: true } && at + 1 < order.Count)
            {
                var next = order[at + 1];
                listed[next.Number] = await ListSeasonAsync(t, src, next, false, ct, outcome).ConfigureAwait(false);
            }
            var seasons = new List<LibrarySeason>();
            var pending = new List<SeasonFolder>();
            foreach (var f in order)
            {
                if (listed.TryGetValue(f.Number, out var l)) seasons.Add(l);
                else if (_episodesCache.TryGet($"{key}|{f.Id}", out var kept, out _)) seasons.Add(kept);
                else { seasons.Add(new LibrarySeason(f.Number, f.Label, Array.Empty<MediaItemViewModel>(), src.Service, Pending: true)); pending.Add(f); }
            }
            ScheduleRest(t, src, pending, active.Number);
            return (!outcome.Failed, seasons);
        }

        private Task<LibrarySeason> ListSeasonAsync(CatalogueTitle t, TitleSource src, SeasonFolder f, bool fresh, CancellationToken ct, Outcome? outcome) =>
            CachedAsync(_episodesCache, $"{KeyOf(src)}|{f.Id}", fresh, async c =>
            {
                var inFolder = await Children(f.Id, t, $"{t.Name} {f.Label} from {src.Service}", c).ConfigureAwait(false);
                var eps = (inFolder ?? Enumerable.Empty<MediaItemViewModel>()).Where(e => e.Type != Baird.Services.MediaType.Brand).ToList();
                return (inFolder != null, new LibrarySeason(f.Number, f.Label, eps, src.Service, f.YouTube ? YouTubeService.NextPageId(f.Id, eps.Count) : null));
            }, ct, outcome, t.Key);

        /// <summary>Hands the seasons not listed yet to the <see cref="BackgroundSink"/>, nearest the opening season first (later before earlier on a tie).</summary>
        private void ScheduleRest(CatalogueTitle t, TitleSource src, List<SeasonFolder> pending, int active)
        {
            var sink = BackgroundSink;
            if (sink == null || pending.Count == 0) return;
            var batch = new List<(string, Func<CancellationToken, Task>)>();
            foreach (var f in pending.OrderBy(f => Math.Abs(f.Number - active)).ThenByDescending(f => f.Number))
            {
                var key = $"{KeyOf(src)}|{f.Id}";
                lock (_refreshing)
                {
                    if (_refreshing.TryGetValue(key, out var since) && _clock() - since < RefreshLost) continue;
                    _refreshing[key] = _clock();
                }
                var folder = f;
                batch.Add((key, async c =>
                {
                    try { await ListSeasonIntoAsync(t, src, folder, true, c).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Console.WriteLine($"[ShowData] Listing {t.Name} {folder.Label} failed: {ex.Message}"); }
                    finally { lock (_refreshing) _refreshing.Remove(key); }
                }
                ));
            }
            if (batch.Count > 0) sink(batch);
        }

        /// <summary>Lists one season of a source and puts it where its page will find it, telling the page (a season that failed to list stays "–").</summary>
        private async Task ListSeasonIntoAsync(CatalogueTitle t, TitleSource src, SeasonFolder f, bool fresh, CancellationToken ct)
        {
            var outcome = new Outcome();
            var season = await ListSeasonAsync(t, src, f, fresh, ct, outcome).ConfigureAwait(false);
            if (outcome.Failed) return;
            // The source's seasons as the page last saw them, with this one in place of its "–".
            var key = KeyOf(src);
            if (_seasonCache.TryGet(key, out var kept, out _))
            {
                var patched = kept.Where(x => x.Number != season.Number).Append(season).OrderBy(x => x.Number == 0 ? int.MaxValue : x.Number).ToList();
                _seasonCache.Put(key, patched);
            }
            RaiseChanged(t.Key, ShowChange.Availability);
        }

        /// <summary>
        /// For the warming queue (and a season tab the viewer lit that is still "–"): lists one season of the title's sources that list a season
        /// at a time, if it isn't listed yet. Returns whether anything was listed. Never throws but for cancellation of <paramref name="ct"/>.
        /// </summary>
        public async Task<bool> EnsureSeasonListedAsync(CatalogueTitle title, int number, CancellationToken ct = default)
        {
            var any = false;
            try
            {
                foreach (var src in title.Sources.Where(s => IsCostly(s.Service)))
                {
                    IReadOnlyList<SeasonFolder>? folders;
                    lock (_folders) _folders.TryGetValue(KeyOf(src), out folders);
                    if (folders?.FirstOrDefault(x => x.Number == number) is not { } f) continue;
                    if (_episodesCache.TryGet($"{KeyOf(src)}|{f.Id}", out _, out var stale) && !stale) continue;
                    await ListSeasonIntoAsync(title, src, f, true, ct).ConfigureAwait(false);
                    any = true;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Console.WriteLine($"[ShowData] Listing {title.Name} season {number} failed: {ex.Message}"); }
            return any;
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

        private static readonly Regex EpisodeCode = new(@"^S\d+[ :]E(\d+)$", RegexOptions.Compiled);

        /// <summary>The episode number the service gave an episode ("S1 E3" → 3), or null when it gave none.</summary>
        private static int? RealNumberOf(MediaItemViewModel m) =>
            EpisodeCode.Match(m.Details ?? "") is { Success: true } c && int.TryParse(c.Groups[1].Value, out var n) && n > 0 ? n : null;

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
                if (lib != null)
                {
                    // The service's own episode numbers (Jellyfin's "S1 E3") when every episode has one: a listing that lacks the first
                    // episode, or is in another order, would otherwise be numbered by position, and "episode 3" here would be another
                    // episode than the one Continue watching calls S1 E3.
                    var real = lib.Episodes.Select(RealNumberOf).ToList();
                    if (lib.Episodes.Count > 0 && real.All(x => x != null) && real.Distinct().Count() == real.Count)
                        eps.AddRange(lib.Episodes.Zip(real, (m, r) => (m, r: r!.Value)).OrderBy(x => x.r).Select(x => EpisodeOf(x.m, n, x.r)));
                    else eps.AddRange(lib.Episodes.Select((m, i) => EpisodeOf(m, n, i + 1)));
                }
                var count = seerr?.EpisodeCount ?? 0;
                var named = seasonDetails != null && seasonDetails.TryGetValue(n, out var sd) ? sd.Episodes : null;
                var have = eps.Select(x => x.Number).ToHashSet();
                for (var e = 1; e <= count; e++)
                {
                    if (have.Contains(e)) continue;
                    var info = named?.FirstOrDefault(x => x.Number == e);
                    var name = string.IsNullOrWhiteSpace(info?.Name) ? $"Episode {e}" : info!.Name;
                    eps.Add(new Episode(n, e, $"{e}. {name}", null, info?.StillUrl, Pending(e), Download: Dl(e), Synopsis: info?.Overview));
                }
                if (have.Count > 0 && eps.Count > have.Count) eps = eps.OrderBy(x => x.Number).ToList();

                var label = lib?.Label ?? (n == 0 ? "Specials" : $"Season {n}");
                if (lib is { Pending: true })
                    // The service has it, its episodes aren't listed yet: shown as there, and "–" until they are.
                    seasons.Add(new Season(n, label, SeasonState.Available, lib.Service ?? service, eps, count > 0 ? count : null, 0, null, null, Pending: true));
                else if (lib != null && lib.Episodes.Count > 0)
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
