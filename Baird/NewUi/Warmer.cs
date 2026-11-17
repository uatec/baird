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
    /// (its own data, then its children, then its siblings) and a newer batch runs ahead of older ones. A title is warmed by
    /// refreshing its kept metadata when missing or older than a week (<see cref="ShowData.EnsureFreshAsync"/>: no network for a
    /// fresh record) and fetching its backdrop and opening season's stills; a season by naming it (<see cref="ShowData.EnsureSeasonAsync"/>)
    /// and fetching its stills. A warmed title's own links are never queued, and stream resolution is never warmed.
    /// Keys: <c>show:{title key}</c> for a title, <c>show:{title key}#season:{n}</c> for one of its seasons.
    /// </summary>
    public sealed class Warmer
    {
        /// <summary>How long a card must stay lit before its page is warmed ahead of everything.</summary>
        public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);
        /// <summary>At most this many images are fetched for one title (or one season) per job.</summary>
        public const int MaxImages = 12;

        private readonly WorkQueue _queue;
        private readonly ShowData _shows;
        private readonly ITimers _timers;
        private readonly Func<string, Task> _warm;
        private readonly object _lock = new();
        private string? _litKey;
        private IDisposable? _pending;
        private CancellationTokenSource? _litCts;
        private (string Title, int Index)? _litSeason;
        private IReadOnlyList<CatalogueTitle>? _from;

        /// <summary>Every item handed to the queue (key, rank, urgent): for tests and diagnostics.</summary>
        public event Action<string, Rank, bool>? Queued;

        public Warmer(WorkQueue queue, ShowData shows, ITimers timers, Func<string, Task>? warmImage = null)
        {
            _queue = queue; _shows = shows; _timers = timers; _warm = warmImage ?? ImageCache.WarmAsync;
        }

        public static string KeyOf(CatalogueTitle t) => "show:" + t.Key;
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
        public void PageOpened(string pageKey, IReadOnlyList<CatalogueTitle> children, IReadOnlyList<CatalogueTitle> siblings)
        {
            var batch = children.Select(t => TitleJob(t, Rank.Child, null))
                .Concat(siblings.Select(t => TitleJob(t, Rank.Sibling, null))).ToList();
            Console.WriteLine($"[Warmer] {pageKey}: {children.Count} children, {siblings.Count} siblings");
            Submit(batch);
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
            batch.AddRange(SeasonOrder(d).Select(i => SeasonJob(self, d.Seasons[i].Number, Rank.Child)));
            batch.AddRange(siblings.Select(t => TitleJob(t, Rank.Sibling, null)));
            Submit(batch);
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
                Urgent(TitleJob(title, Rank.Page, null, cts.Token));
            });
        }

        /// <summary>A season tab is lit on a show page: a season not loaded yet is warmed ahead of everything; the seasons either side follow as a new batch.</summary>
        public void Season(ShowDetails d, int index)
        {
            if (index < 0 || index >= d.Seasons.Count || _litSeason == (d.Title.Key, index)) return;
            _litSeason = (d.Title.Key, index);
            if (ShowData.Unnamed(d.Seasons[index])) Urgent(SeasonJob(d.Title, d.Seasons[index].Number, Rank.Page));
            Submit(new[] { index + 1, index - 1 }.Where(i => i >= 0 && i < d.Seasons.Count)
                .Select(i => SeasonJob(d.Title, d.Seasons[i].Number, Rank.Child)).ToList());
        }

        // ------------------------------------------------------------------ jobs

        /// <summary>
        /// A title: its kept metadata made fresh (no network when it is), then its backdrop and opening season's stills, from the
        /// details given (a page that has them) or the kept record. Nothing it links to is queued.
        /// </summary>
        private WorkItem TitleJob(CatalogueTitle t, Rank rank, ShowDetails? known, CancellationToken stop = default) => new(KeyOf(t), rank, async ct =>
        {
            using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
            try
            {
                await _shows.EnsureFreshAsync(t, both.Token).ConfigureAwait(false);
                if ((known ?? _shows.Stored(t)) is { } d) await WarmAsync(UrlsFor(d), both.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (both.IsCancellationRequested) { }
        });

        /// <summary>One season of a title: named when it's only numbered (Seerr), then its stills.</summary>
        private WorkItem SeasonJob(CatalogueTitle t, int number, Rank rank) => new(KeyOf(t, number), rank, async ct =>
        {
            try { await WarmAsync(await _shows.EnsureSeasonAsync(t, number, ct).ConfigureAwait(false), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        });

        private async Task WarmAsync(IEnumerable<string> urls, CancellationToken ct)
        {
            foreach (var url in urls.Distinct().Take(MaxImages))
            {
                ct.ThrowIfCancellationRequested();
                await _warm(url).ConfigureAwait(false); // one at a time: cheap on the Pi
            }
        }

        private void Submit(IReadOnlyList<WorkItem> batch)
        {
            if (batch.Count == 0) return;
            foreach (var item in batch) Queued?.Invoke(item.Key, item.Rank, false);
            _queue.Submit(batch);
        }

        private void Urgent(WorkItem item)
        {
            Queued?.Invoke(item.Key, item.Rank, true);
            _queue.Urgent(item);
        }
    }
}
