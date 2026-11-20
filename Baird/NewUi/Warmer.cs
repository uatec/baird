using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.NewUi.Adapters;
using Baird.NewUi.Controls;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;

namespace Baird.NewUi
{
    /// <summary>
    /// Warms what a page links to directly, one hop only, through the <see cref="WorkQueue"/>: each page opened submits one batch
    /// (its own data, then its children, then its siblings) and a newer batch runs ahead of older ones. A title in a batch is warmed by
    /// refreshing its kept metadata when missing or older than about a week (<see cref="ShowData.FreshFor"/>; no network for a fresh
    /// record), never by searching the providers nor listing YouTube or a browser service (<see cref="BatchJob"/>), and its backdrop goes
    /// to the disk cache only; a season by naming it (<see cref="ShowData.EnsureSeasonAsync"/>) and fetching its stills. Only what is about
    /// to be on screen is decoded: the lit card, the page's own title, its opening season and those either side, the lit season tab.
    /// The lit card's page is worked out exactly as the page will (<see cref="ShowData.DetailsAsync"/>), so the page joins it.
    /// A warmed title's own links are never queued, and stream resolution is never warmed.
    /// Keys: <c>show:{title key}</c> for a title, <c>show:{title key}#season:{n}</c> for one of its seasons.
    /// </summary>
    public sealed class Warmer
    {
        /// <summary>How long a card must stay lit before its page is warmed ahead of everything.</summary>
        public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);
        /// <summary>At most this many images are fetched for one title (or one season) per job.</summary>
        public const int MaxImages = 12;

        private readonly WorkQueue _queue;
        /// <summary>The costly lane (browser services, YouTube): one job at a time of its own, so it never holds up <see cref="_queue"/>. Null: it isn't warmed.</summary>
        private readonly WorkQueue? _costly;
        private readonly ShowData _shows;
        private readonly ITimers _timers;
        private readonly Func<string, Task> _warm;      // fetched and decoded, ready to draw (ImageCache): only what's about to be on screen
        private readonly Func<string, Task> _warmDisk;  // fetched to the disk cache only, never decoded: everything else
        private readonly Dictionary<string, long> _generations = new(); // per page (Home, Search): its latest batch
        private readonly object _lock = new();
        private string? _litKey;
        private IDisposable? _pending;
        private CancellationTokenSource? _litCts;
        private (string Title, int Index)? _litSeason;
        private IReadOnlyList<CatalogueTitle>? _from;

        /// <summary>Every item handed to the queue (key, rank, urgent): for tests and diagnostics.</summary>
        public event Action<string, Rank, bool>? Queued;

        /// <param name="warmImage">Fetch and decode one image into the <see cref="ImageCache"/> (default <see cref="ImageCache.WarmAsync"/>).</param>
        /// <param name="warmDisk">Fetch one image's bytes to the disk cache, no decode (default <see cref="ImageCache.WarmDiskAsync"/>).</param>
        /// <param name="costly">The lane for work that drives the browser or yt-dlp (<see cref="ShowData.IsCostly"/>); without one that availability isn't warmed.</param>
        public Warmer(WorkQueue queue, ShowData shows, ITimers timers, Func<string, Task>? warmImage = null, Func<string, Task>? warmDisk = null, WorkQueue? costly = null)
        {
            _costly = costly;
            _queue = queue; _shows = shows; _timers = timers; _warm = warmImage ?? ImageCache.WarmAsync; _warmDisk = warmDisk ?? ImageCache.WarmDiskAsync;
        }

        /// <summary>
        /// A title whose source is costly to list in the background: YouTube (every listing is a yt-dlp process) or a service played in
        /// the browser (its listings drive the one shared browser). Batches never compute these: their pages do, when opened.
        /// </summary>
        public static bool CostlyToList(CatalogueTitle t) =>
            t.Sources.Any(s => s.Service == Baird.Services.YouTubeService.SourceName || HomeData.ViaOf(s.Service) == PlayVia.Browser);

        /// <summary>Does a title's availability need the costly lane: it has no source (the browser's providers are searched too) or a costly one.</summary>
        public static bool NeedsCostly(CatalogueTitle t) => t.Sources.Count == 0 || CostlyToList(t);

        public static string KeyOf(CatalogueTitle t) => "show:" + t.Key;
        private static string CostlyKeyOf(CatalogueTitle t) => "costly:" + KeyOf(t);
        public static string KeyOf(CatalogueTitle t, int season) => $"show:{t.Key}#season:{season}";

        /// <summary>Does OK on this card open a show page (worth warming)?</summary>
        public static bool OpensAPage(HomeCard c) => c.Title is { } t
            ? t.Kind != TitleKind.Episode && (t.Kind == TitleKind.Series || t.Best == null)
            : c.Kind == CardKind.Series || c.Source == "Seerr";

        /// <summary>The pages these cards open, in the cards' order, each once (channels and cards that play are skipped).</summary>
        public static IReadOnlyList<CatalogueTitle> TitlesOf(IEnumerable<HomeCard> cards)
        {
            var seen = new HashSet<string>();
            var titles = new List<CatalogueTitle>();
            foreach (var c in cards)
            {
                if (!OpensAPage(c)) continue;
                var t = ShowData.FromCard(c);
                if (seen.Add(t.Key)) titles.Add(t);
            }
            return titles;
        }

        /// <summary>The season a show page opens on: the next episode's, else the first with something to watch.</summary>
        public static int OpeningSeason(ShowDetails d)
        {
            if (d.Seasons.Count == 0) return -1;
            var next = d.Next?.Episode;
            var i = next != null ? d.Seasons.ToList().FindIndex(s => s.Number == next.Season) : d.Seasons.ToList().FindIndex(s => s.State is SeasonState.Available or SeasonState.Partial);
            return Math.Max(0, i);
        }

        /// <summary>The page's backdrop, then the opening season's stills from where the page will be looking.</summary>
        public static IEnumerable<string> UrlsFor(ShowDetails d)
        {
            if (d.Title.ImageUrl is { Length: > 0 } art) yield return art;
            var i = OpeningSeason(d);
            if (i < 0) yield break;
            var eps = d.Seasons[i].Episodes;
            var from = d.Next?.Episode is { } n && eps.Contains(n) ? Math.Max(0, eps.ToList().IndexOf(n) - 1) : 0;
            foreach (var e in eps.Skip(from)) if (e.ImageUrl is { Length: > 0 } u) yield return u;
        }

        /// <summary>A show page's seasons (indexes) in warming order: the opening season, then nearest first outward (later before earlier on a tie).</summary>
        public static IReadOnlyList<int> SeasonOrder(ShowDetails d)
        {
            var open = OpeningSeason(d);
            if (open < 0) return Array.Empty<int>();
            var order = new List<int> { open };
            for (var step = 1; order.Count < d.Seasons.Count; step++)
            {
                if (open + step < d.Seasons.Count) order.Add(open + step);
                if (open - step >= 0) order.Add(open - step);
            }
            return order;
        }

        /// <summary>A page (Home, Search) was opened or shown again: warm its children, then its siblings, as one new batch.</summary>
        /// <remarks>
        /// A page's new batch supersedes its older ones: their jobs still queued do nothing when they come up (a new search's results
        /// replace the last search's). These are background jobs: see <see cref="BatchJob"/> for what they may cost.
        /// </remarks>
        public void PageOpened(string pageKey, IReadOnlyList<CatalogueTitle> children, IReadOnlyList<CatalogueTitle> siblings)
        {
            long generation;
            lock (_lock) _generations[pageKey] = generation = _generations.GetValueOrDefault(pageKey) + 1;
            var batch = children.Select(t => BatchJob(t, Rank.Child, pageKey, generation))
                .Concat(siblings.Select(t => BatchJob(t, Rank.Sibling, pageKey, generation))).ToList();
            Console.WriteLine($"[Warmer] {pageKey}: {children.Count} children, {siblings.Count} siblings");
            Submit(batch);
            SubmitCostly(children.Where(NeedsCostly).Select(t => CostlyJob(t, Rank.Child, pageKey, generation))
                .Concat(siblings.Where(NeedsCostly).Select(t => CostlyJob(t, Rank.Sibling, pageKey, generation))).ToList());
        }

        /// <summary>The viewer is opening a page from this row (its other pages are the new page's siblings).</summary>
        public void From(IReadOnlyList<CatalogueTitle> row) { lock (_lock) _from = row; }

        /// <summary>
        /// A show page has its details: one batch of the page's own title, then its seasons (<see cref="SeasonOrder"/>), then its
        /// siblings (the row it was opened from, when <see cref="From"/> named one holding it; else none).
        /// </summary>
        public void ShowPageOpened(ShowDetails d)
        {
            var self = d.Title;
            IReadOnlyList<CatalogueTitle> row;
            lock (_lock) { row = _from ?? Array.Empty<CatalogueTitle>(); _from = null; }
            _litSeason = null; // a page (re)opened: its season tabs are new to the viewer again
            var siblings = row.Any(t => t.Key == self.Key) ? row.Where(t => t.Key != self.Key).ToList() : new List<CatalogueTitle>();
            var batch = new List<WorkItem> { TitleJob(self, Rank.Page, d) };
            // Only the opening season and those either side are decoded (the viewer is about to see them); the rest go to disk only, so a
            // long show (The Simpsons: 33 seasons) can't push the opening season's stills, or Home's posters, out of the image cache.
            var open = OpeningSeason(d);
            batch.AddRange(SeasonOrder(d).Select(i => SeasonJob(self, d.Seasons[i].Number, Rank.Child, decode: Math.Abs(i - open) <= 1)));
            batch.AddRange(siblings.Select(t => BatchJob(t, Rank.Sibling, null, 0)));
            Submit(batch);
            SubmitCostly(siblings.Where(NeedsCostly).Select(t => CostlyJob(t, Rank.Sibling, null, 0)).ToList());
        }

        /// <summary>The lit card changed (null: nothing worth warming is lit). Once lit for <see cref="Delay"/>, its page is warmed ahead of everything.</summary>
        public void Lit(HomeCard? card)
        {
            var title = card != null && OpensAPage(card) ? ShowData.FromCard(card) : null;
            if (title?.Key == _litKey) return;
            _litKey = title?.Key;
            _pending?.Dispose();
            _pending = null;
            _litCts?.Cancel(); // moving on drops the last lit card's warming, even if it already started
            _litCts = null;
            if (title == null) return;
            _pending = _timers.After(Delay, () =>
            {
                var cts = _litCts = new CancellationTokenSource();
                // Ahead of everything, unless frames are dropping: then it waits its turn like the page's own data (the season tab, which
                // the viewer navigates to on a page, stays urgent).
                var item = LitJob(title, cts.Token);
                var urgent = _queue.UrgentUnlessPaused(item);
                Queued?.Invoke(item.Key, item.Rank, urgent);
                if (_costly != null && NeedsCostly(title))
                {
                    var costly = CostlyJob(title, Rank.Page, null, 0, cts.Token);
                    Queued?.Invoke(costly.Key, costly.Rank, _costly.UrgentUnlessPaused(costly));
                }
            });
        }

        /// <summary>A season tab is lit on a show page: a season not loaded yet is warmed ahead of everything; the seasons either side follow as a new batch.</summary>
        public void Season(ShowDetails d, int index)
        {
            if (index < 0 || index >= d.Seasons.Count || _litSeason == (d.Title.Key, index)) return;
            _litSeason = (d.Title.Key, index);
            // The lit tab's season is on screen: decoded. Those either side only reach the disk (a viewer running along 33 tabs would
            // otherwise decode two seasons of stills per tab).
            if (ShowData.Unnamed(d.Seasons[index])) Urgent(SeasonJob(d.Title, d.Seasons[index].Number, Rank.Page, decode: true));
            Submit(new[] { index + 1, index - 1 }.Where(i => i >= 0 && i < d.Seasons.Count)
                .Select(i => SeasonJob(d.Title, d.Seasons[i].Number, Rank.Child, decode: false)).ToList());
        }

        // ------------------------------------------------------------------ jobs

        /// <summary>
        /// The lit card's page, exactly as the page will ask for it (<see cref="ShowData.DetailsAsync"/>: the live availability too, so
        /// the page joins it and opens with Play), then its backdrop and opening season's stills, decoded. Dropped when the card is.
        /// </summary>
        private WorkItem LitJob(CatalogueTitle t, CancellationToken stop) => new(KeyOf(t), Rank.Page, async ct =>
        {
            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
            try
            {
                ShowDetails? d;
                if (NeedsCostly(t))
                {
                    // No source, or one that drives the browser or yt-dlp: that part is the costly lane's (CostlyJob), and nothing here waits
                    // on it. What Seerr knows and the cheap sources say is enough to have the page opening populated.
                    if (t.Sources.Count == 0) await _shows.EnsureFreshFromSeerrAsync(t, both.Token).ConfigureAwait(false);
                    await _shows.WarmAvailabilityAsync(t, ShowData.WarmLane.Cheap, both.Token).ConfigureAwait(false);
                    d = _shows.Stored(t);
                }
                else d = await _shows.DetailsAsync(t, null, both.Token).ConfigureAwait(false);
                if (d != null) await WarmAsync(UrlsFor(d), both.Token, decode: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (both.IsCancellationRequested) { }
            catch (ShowLoadTimeoutException) { } // nothing learnt in time: the page asks again when opened
        });

        /// <summary>
        /// A title's availability from the services that drive the browser or yt-dlp, in the costly lane (one job at a time, never in the
        /// way of the cheap one): a title with one of those as its source is made fresh from it (its seasons are listed); one with no
        /// source has those providers searched for it (<see cref="ShowData.WarmAvailabilityAsync"/>). What is learnt waits in the
        /// availability caches (60 s fresh, then served stale while refreshed), so its page doesn't ask the browser itself.
        /// </summary>
        private WorkItem CostlyJob(CatalogueTitle t, Rank rank, string? pageKey, long generation, CancellationToken? stop = null) => new(CostlyKeyOf(t), rank, async ct =>
        {
            if (pageKey != null) lock (_lock) if (_generations.GetValueOrDefault(pageKey) != generation) return;
            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, stop ?? CancellationToken.None);
            try
            {
                if (t.Sources.Count > 0) await _shows.EnsureFreshAsync(t, both.Token).ConfigureAwait(false);
                else await _shows.WarmAvailabilityAsync(t, ShowData.WarmLane.Costly, both.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (both.IsCancellationRequested) { }
        });

        /// <summary>
        /// A show page's own title: its backdrop and opening season's stills, decoded, from the page's details. No network for its data:
        /// the page has just worked it out (and kept it when every piece answered; one that failed is asked again when the page is next
        /// opened, not here). Nothing it links to is queued.
        /// </summary>
        private WorkItem TitleJob(CatalogueTitle t, Rank rank, ShowDetails known) => new(KeyOf(t), rank, async ct =>
        {
            try { await WarmAsync(UrlsFor(known), ct, decode: true).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        });

        /// <summary>
        /// A title in a background batch (Home's cards, search results, a page's siblings). Never searches the providers and never lists a
        /// source that is costly to list (<see cref="CostlyToList"/>: yt-dlp, the browser): a title with no source learns only what Seerr
        /// knows (<see cref="ShowData.EnsureFreshFromSeerrAsync"/>); one with a source is made fresh from its own source when its record is
        /// missing or stale (<see cref="ShowData.EnsureFreshAsync"/>). Then its backdrop, to disk only. A job of a superseded batch does nothing.
        /// </summary>
        private WorkItem BatchJob(CatalogueTitle t, Rank rank, string? pageKey, long generation) => new(KeyOf(t), rank, async ct =>
        {
            if (pageKey != null) lock (_lock) if (_generations.GetValueOrDefault(pageKey) != generation) return;
            try
            {
                if (t.Sources.Count == 0) await _shows.EnsureFreshFromSeerrAsync(t, ct).ConfigureAwait(false);
                else if (!CostlyToList(t)) await _shows.EnsureFreshAsync(t, ct).ConfigureAwait(false);
                await _shows.WarmAvailabilityAsync(t, ShowData.WarmLane.Cheap, ct).ConfigureAwait(false); // where the cheap sources have it
                if ((_shows.Stored(t)?.Title.ImageUrl ?? t.ImageUrl) is { Length: > 0 } art) await WarmAsync(new[] { art }, ct, decode: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        });

        /// <summary>One season of a title: named when it's only numbered (Seerr), then its stills (decoded, or to disk only).</summary>
        private WorkItem SeasonJob(CatalogueTitle t, int number, Rank rank, bool decode) => new(KeyOf(t, number), rank, async ct =>
        {
            try { await WarmAsync(await _shows.EnsureSeasonAsync(t, number, ct).ConfigureAwait(false), ct, decode).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        });

        private async Task WarmAsync(IEnumerable<string> urls, CancellationToken ct, bool decode)
        {
            var warm = decode ? _warm : _warmDisk;
            foreach (var url in urls.Distinct().Take(MaxImages))
            {
                ct.ThrowIfCancellationRequested();
                await warm(url).ConfigureAwait(false); // one at a time: cheap on the Pi
            }
        }

        private void Submit(IReadOnlyList<WorkItem> batch)
        {
            if (batch.Count == 0) return;
            foreach (var item in batch) Queued?.Invoke(item.Key, item.Rank, false);
            _queue.Submit(batch);
        }

        private void SubmitCostly(IReadOnlyList<WorkItem> batch)
        {
            if (_costly == null || batch.Count == 0) return;
            foreach (var item in batch) Queued?.Invoke(item.Key, item.Rank, false);
            _costly.Submit(batch);
        }

        /// <summary>Background jobs waiting or running, across both lanes (the debug overlay).</summary>
        public int Pending => _queue.Pending + (_costly?.Pending ?? 0);

        private void Urgent(WorkItem item)
        {
            Queued?.Invoke(item.Key, item.Rank, true);
            _queue.Urgent(item);
        }
    }
}
