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
    /// Gets the next page ready while you look at its card: when a card that opens a show page has been lit for a moment,
    /// its details are worked out (shared with the page if you open it) and its backdrop and opening season's stills are
    /// fetched, so the page draws at once. One at a time, a dozen images at most, dropped as soon as you move on.
    /// On a show page, lighting a season tab fetches the neighbouring seasons' stills.
    /// </summary>
    public sealed class Prefetcher
    {
        public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);
        public const int MaxImages = 12;

        private readonly ShowData _shows;
        private readonly ITimers _timers;
        private readonly Func<string, Task> _warm;
        private string? _litKey;
        private IDisposable? _pending;
        private CancellationTokenSource? _cts, _seasonCts;

        /// <summary>The prefetch running now (tests await it).</summary>
        public Task Current { get; private set; } = Task.CompletedTask;
        public Task CurrentSeasons { get; private set; } = Task.CompletedTask;

        public Prefetcher(ShowData shows, ITimers timers, Func<string, Task>? warm = null)
        {
            _shows = shows; _timers = timers; _warm = warm ?? ImageCache.WarmAsync;
        }

        /// <summary>Does OK on this card open a show page (worth preparing)?</summary>
        public static bool OpensAPage(HomeCard c) => c.Title is { } t
            ? t.Kind != TitleKind.Episode && (t.Kind == TitleKind.Series || t.Best == null)
            : c.Kind == CardKind.Series || c.Source == "Seerr";

        /// <summary>The lit card changed (null: nothing worth preparing is lit).</summary>
        public void Lit(HomeCard? card)
        {
            var title = card != null && OpensAPage(card) ? ShowData.FromCard(card) : null;
            if (title?.Key == _litKey) return;
            _litKey = title?.Key;
            _pending?.Dispose();
            _cts?.Cancel();
            _cts = null;
            if (title == null) return;
            _pending = _timers.After(Delay, () =>
            {
                var cts = _cts = new CancellationTokenSource();
                Current = RunAsync(title, cts.Token);
            });
        }

        private async Task RunAsync(CatalogueTitle title, CancellationToken ct)
        {
            try
            {
                var d = await _shows.DetailsAsync(title, null, ct).ConfigureAwait(false);
                await WarmAsync(UrlsFor(d), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[Prefetch] {title.Name}: {ex.Message}"); }
        }

        private async Task WarmAsync(IEnumerable<string> urls, CancellationToken ct)
        {
            foreach (var url in urls.Distinct().Take(MaxImages))
            {
                ct.ThrowIfCancellationRequested();
                await _warm(url).ConfigureAwait(false); // one at a time: cheap on the Pi
            }
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

        /// <summary>A season tab is lit on a show page: fetch the stills of the seasons either side.</summary>
        public void Season(ShowDetails d, int index)
        {
            _seasonCts?.Cancel();
            var cts = _seasonCts = new CancellationTokenSource();
            var urls = new[] { index + 1, index - 1 }.Where(i => i >= 0 && i < d.Seasons.Count)
                .SelectMany(i => d.Seasons[i].Episodes.Take(MaxImages / 2)).Select(e => e.ImageUrl).OfType<string>().Where(u => u.Length > 0).ToList();
            CurrentSeasons = urls.Count == 0 ? Task.CompletedTask : Task.Run(async () =>
            {
                try { await WarmAsync(urls, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            });
        }
    }
}
