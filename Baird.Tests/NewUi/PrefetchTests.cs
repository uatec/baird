using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Baird.NewUi;
using Baird.NewUi.Adapters;
using Baird.NewUi.Controls;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Screens;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>The next page is ready before you open it: details shared and kept briefly, artwork fetched while a card is lit.</summary>
    public class PrefetchTests
    {
        private static (Prefetcher P, CatalogueTests.World W, PlaybackControllerTests.FakeTimers T, List<string> Warmed) Build()
        {
            var w = CatalogueTests.Build();
            var t = new PlaybackControllerTests.FakeTimers();
            var warmed = new List<string>();
            var p = new Prefetcher(w.Shows, t, url => { lock (warmed) warmed.Add(url); return Task.CompletedTask; });
            return (p, w, t, warmed);
        }

        private static async Task<CatalogueTitle> Simpsons(CatalogueTests.World w) =>
            (await w.Search.SearchAsync("simpsons", CatalogueTests.Channels, DateTimeOffset.UtcNow)).Top!.Title!;

        [Fact]
        public async Task A_lit_series_gets_its_page_and_artwork_ready_after_a_moment()
        {
            var (p, w, t, warmed) = Build();
            w.Jellyfin.Children["simp-s1"] = w.Jellyfin.Children["simp-s1"].Select(e => e with { ImageUrl = $"http://img/{e.Id}" }).ToList();
            var title = await Simpsons(w);
            p.Lit(CatalogueSearch.CardOf(title));
            Assert.False(w.Shows.IsKnown(title)); // not yet: it waits until the card has been lit a moment
            t.Advance(Prefetcher.Delay);
            await p.Current;
            Assert.True(w.Shows.IsKnown(title));
            Assert.Equal(new[] { title.ImageUrl!, "http://img/s1e1", "http://img/s1e2" }, warmed);
        }

        [Fact]
        public async Task Moving_on_quickly_prefetches_only_where_you_stop_and_films_that_play_need_nothing()
        {
            var (p, w, t, warmed) = Build();
            var title = await Simpsons(w);
            var severance = (await w.Search.SearchAsync("severance", CatalogueTests.Channels, DateTimeOffset.UtcNow)).Top!.Title!;
            var alien = (await w.Search.SearchAsync("alien", CatalogueTests.Channels, DateTimeOffset.UtcNow)).Top!;
            p.Lit(CatalogueSearch.CardOf(title));
            t.Advance(TimeSpan.FromMilliseconds(100));
            p.Lit(CatalogueSearch.CardOf(severance));
            t.Advance(Prefetcher.Delay);
            await p.Current;
            Assert.False(w.Shows.IsKnown(title));
            Assert.True(w.Shows.IsKnown(severance));

            Assert.False(Prefetcher.OpensAPage(alien.Card)); // OK plays it: no page to prepare
            Assert.True(Prefetcher.OpensAPage(new HomeCard(CardKind.Film, "tmdb:movie:1", "X", null, null, null, "get", "Seerr", PlayVia.None)));
        }

        [Fact]
        public async Task The_page_uses_what_the_prefetch_learnt_and_asks_again_only_when_told()
        {
            var w = CatalogueTests.Build();
            var title = await Simpsons(w);
            var first = await w.Shows.DetailsAsync(title);
            Assert.Same(first, await w.Shows.DetailsAsync(title));
            Assert.NotSame(first, await w.Shows.DetailsAsync(title, fresh: true)); // after a request, or polling a download
        }

        [Fact]
        public async Task A_page_opened_while_the_prefetch_runs_joins_it_and_sees_what_is_known_so_far()
        {
            var (search, shows, jf, _) = CatalogueTests.Slow();
            var loki = ShowData.FromCard(new HomeCard(CardKind.Series, "tmdb:tv:84958", "Loki", "2021", null, null, null, "Seerr", PlayVia.None));
            using var prefetchCts = new CancellationTokenSource();
            var prefetch = shows.DetailsAsync(loki, null, prefetchCts.Token);
            await Task.Delay(150);
            var seen = new List<ShowDetails>();
            var page = shows.DetailsAsync(loki, d => { lock (seen) seen.Add(d); });
            lock (seen) Assert.True(seen.Single().CheckingLibrary); // at once: Seerr's view, still checking the library
            prefetchCts.Cancel(); // the prefetch moves on; the page still wants it, so it carries on
            jf.Gate.SetResult();
            var d = await page;
            Assert.Equal("Play S1 E1", d.Next!.Value.Label);
            Assert.Equal(1, jf.Searches);
        }

        [AvaloniaFact]
        public void Prefetched_artwork_is_drawn_at_once_with_no_fade()
        {
            ImageCache.Clear();
            var bmp = new WriteableBitmap(new PixelSize(16, 9), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            ImageCache.Put("http://img/ready", bmp);
            var art = new ArtImage("http://img/ready");
            Assert.Same(bmp, art.Main.Source);
            Assert.Equal(1, art.Opacity);
        }

        [AvaloniaFact]
        public async Task Lighting_a_season_tab_fetches_the_seasons_either_side()
        {
            var (p, w, _, warmed) = Build();
            w.Jellyfin.Children["simp-s2"] = w.Jellyfin.Children["simp-s2"].Select(e => e with { ImageUrl = $"http://img/{e.Id}" }).ToList();
            var d = await w.Shows.DetailsAsync(await Simpsons(w));
            p.Season(d, 0);
            await p.CurrentSeasons;
            Assert.Equal(new[] { "http://img/s2e1" }, warmed);
        }

        [AvaloniaFact]
        public async Task Search_and_show_page_hand_the_lit_card_to_the_prefetcher()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows; rig.Ctx.Channels = CatalogueTests.Channels; rig.Ctx.Timers = rig.Timers;
            rig.Ctx.Prefetch = new Prefetcher(w.Shows, rig.Timers, _ => Task.CompletedTask);
            var s = new SearchScreen(rig.Ctx);
            s.SetQuery("simpsons");
            await s.RunAsync();
            Dispatcher.UIThread.RunJobs();
            for (var i = 0; i < 12; i++) s.OnAction(UiAction.Right, false);
            rig.Timers.Advance(Prefetcher.Delay);
            await rig.Ctx.Prefetch.Current;
            Assert.True(w.Shows.IsKnown(s.ResultRows[0][0].Title!));
        }
    }
}
