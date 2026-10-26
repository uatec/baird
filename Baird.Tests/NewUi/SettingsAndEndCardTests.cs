using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Baird.NewUi.Adapters;
using Baird.NewUi.Controls;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.NewUi.Screens;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    /// <summary>Settings (subtitles, autoplay, sources, about), the end card, the top result's pill, the show page's extras.</summary>
    public class SettingsAndEndCardTests
    {
        private sealed class Saved
        {
            public bool Subs = true, Autoplay = true;
            public readonly List<string> Writes = new();
        }

        private static (SettingsScreen S, Saved Saved) Settings(Func<Task<IReadOnlyList<SourceStatus>>>? sources = null)
        {
            var saved = new Saved();
            var options = new SettingsOptions(() => saved.Subs, v => { saved.Subs = v; saved.Writes.Add($"Subtitles={v}"); },
                () => saved.Autoplay, v => { saved.Autoplay = v; saved.Writes.Add($"Autoplay={v}"); },
                sources ?? (() => Task.FromResult<IReadOnlyList<SourceStatus>>(new[] { new SourceStatus("Jellyfin", "Connected", SourceHealth.Ok) })),
                About: "1.2.3 · new interface");
            return (new SettingsScreen(() => UiMode.New, _ => { }, 1.0, _ => { }, options: options), saved);
        }

        private static void GoTo(SettingsScreen s, string label)
        {
            for (var i = 0; i < 30 && ((ListRow)s.LitElement!).LabelText != label; i++) s.OnAction(UiAction.Down, false);
            Assert.Equal(label, ((ListRow)s.LitElement!).LabelText);
        }

        [AvaloniaFact]
        public void Subtitles_and_autoplay_switch_with_left_and_right_and_are_saved()
        {
            var (s, saved) = Settings();
            GoTo(s, "Subtitles by default");
            s.OnAction(UiAction.Right, false);
            GoTo(s, "Play the next episode automatically");
            s.OnAction(UiAction.Left, false);
            Assert.Equal(new[] { "Subtitles=False", "Autoplay=False" }, saved.Writes);
            Assert.Contains(("Play the next episode automatically", "‹  Off  ›"), s.RowTexts);
        }

        [AvaloniaFact]
        public async Task Sources_show_their_status_and_about_says_the_version_and_interface()
        {
            var (s, _) = Settings();
            await s.Loaded;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(("Jellyfin", "Connected"), s.RowTexts);
            Assert.Contains(("Netflix", "Checking…"), s.RowTexts); // not reported (yet): says so, never a guess
            Assert.Contains(("About Baird", "1.2.3 · new interface"), s.RowTexts);
            GoTo(s, "About Baird"); // the last row is reachable (the list scrolls)
        }

        private sealed class CountingSeerr : IJellyseerrService
        {
            public int Calls;
            public Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string q, int p, CancellationToken ct = default) => throw new InvalidOperationException();
            public Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int p, CancellationToken ct = default)
            {
                Calls++;
                return Task.FromResult<IEnumerable<JellyseerrSearchResult>>(new[] { new JellyseerrSearchResult { Id = 1, Title = "x", MediaType = "tv" } });
            }
            public Task<JellyseerrRequestResponse> CreateRequestAsync(int id, string t, CancellationToken ct = default) => throw new InvalidOperationException();
            public Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken ct = default) => throw new InvalidOperationException();
        }

        [Fact]
        public async Task Source_status_comes_from_what_the_services_know_and_asks_seerr_at_most_once_a_minute()
        {
            var now = DateTimeOffset.UnixEpoch;
            var seerr = new CountingSeerr();
            bool? netflix = false;
            var reader = new SourceStatusReader(() => 120, () => true, seerr, true, () => netflix, false, () => now);
            var first = await reader.ReadAsync();
            Assert.Equal(new[] { "120 channels", "Connected", "Connected", "Needs signing in", "Not set up" }, first.Select(x => x.Text));
            Assert.Equal(SourceHealth.Warning, first.Single(x => x.Name == "Netflix").Health);
            await reader.ReadAsync();
            Assert.Equal(1, seerr.Calls);
            now += SourceStatusReader.SeerrEvery;
            netflix = null;
            var later = await reader.ReadAsync();
            Assert.Equal(2, seerr.Calls);
            Assert.Equal("Not set up", later.Single(x => x.Name == "Netflix").Text);
        }

        [Fact]
        public void Subtitles_default_applies_at_each_play()
        {
            var e = new PlaybackControllerTests.FakeEngine();
            var t = new PlaybackControllerTests.FakeTimers();
            var c = new PlaybackController(e, a => a(), t, () => t.Now, null) { SubtitlesDefault = false };
            Assert.False(c.SubtitlesOn);
            c.PlayItem("a", "http://a", TimeSpan.Zero, "A");
            e.Loaded();
            Assert.Contains("subs False", e.Calls);
            c.ToggleSubtitles();
            c.PlayItem("b", "http://b", TimeSpan.Zero, "B");
            e.Loaded();
            Assert.Equal("subs True", e.Calls.Last()); // your choice for this sitting carries on
        }

        // ------------------------------------------------------------------ the end card
        private static async Task<(ShowPage Page, HomeScreenTests.Rig Rig)> Simpsons()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows; rig.Ctx.Channels = CatalogueTests.Channels; rig.Ctx.Timers = rig.Timers;
            var r = await w.Search.SearchAsync("simpsons", CatalogueTests.Channels, DateTimeOffset.UtcNow);
            var page = new ShowPage(rig.Ctx, r.Top!.Title!);
            await page.Loaded;
            Dispatcher.UIThread.RunJobs();
            return (page, rig);
        }

        [AvaloniaFact]
        public async Task The_end_card_names_the_show_the_episode_and_where_back_goes()
        {
            var (page, rig) = await Simpsons();
            page.OnAction(UiAction.Ok, false); // Play S1 E1
            var next = rig.Ctx.NextEpisode()!;
            var card = new UpNextCard(rig.Ctx, rig.Timers, next);
            Assert.Equal("Up next in The Simpsons", card.Eyebrow);
            Assert.Equal("Bart the Genius", card.Title);
            Assert.Equal("Season 1, episode 2 · 45 min", card.Meta);
            Assert.Equal(new[] { "Play now", "Back to The Simpsons" }, card.Buttons);
        }

        [AvaloniaFact]
        public async Task With_autoplay_off_the_end_card_waits_for_ok()
        {
            var (page, rig) = await Simpsons();
            page.OnAction(UiAction.Ok, false);
            rig.Ctx.Autoplay = false;
            var card = new UpNextCard(rig.Ctx, rig.Timers, rig.Ctx.NextEpisode()!);
            Assert.False(card.CountingDown);
            for (var i = 0; i < 20; i++) rig.Timers.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("s1e1", rig.Ctx.Playback.Current!.ItemId);
            card.OnAction(UiAction.Ok, false);
            Assert.Equal("s1e2", rig.Ctx.Playback.Current!.ItemId);
        }

        // ------------------------------------------------------------------ search and show-page extras
        [AvaloniaFact]
        public async Task The_top_result_says_what_ok_does()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows; rig.Ctx.Channels = CatalogueTests.Channels; rig.Ctx.Timers = rig.Timers;
            async Task<string?> Pill(string q)
            {
                var s = new SearchScreen(rig.Ctx);
                foreach (var ch in q) s.OnAction(char.IsDigit(ch) ? UiAction.Digit0 + (ch - '0') : UiAction.Del, false);
                if (!q.All(char.IsDigit)) s.SetQuery(q);
                rig.Timers.Advance(SearchScreen.Debounce);
                await s.RunAsync();
                Dispatcher.UIThread.RunJobs();
                return s.TopPillText;
            }
            Assert.Equal("⤓ Request", await Pill("severance"));
            Assert.Equal("▶ Play", await Pill("alien"));
            Assert.Equal("Live", await Pill("104"));
        }

        [AvaloniaFact]
        public async Task Watched_episodes_carry_a_tick()
        {
            var rig = HomeScreenTests.Context();
            var w = CatalogueTests.Build();
            w.History.Add(new Baird.Models.HistoryItem { Id = "s1e1", LastPosition = TimeSpan.FromMinutes(22), Duration = TimeSpan.FromMinutes(22), IsFinished = true, LastWatched = DateTime.Now });
            rig.Ctx.Search = w.Search; rig.Ctx.Shows = w.Shows;
            var r = await w.Search.SearchAsync("simpsons", CatalogueTests.Channels, DateTimeOffset.UtcNow);
            var page = new ShowPage(rig.Ctx, r.Top!.Title!);
            await page.Loaded;
            Dispatcher.UIThread.RunJobs();
            page.OnAction(UiAction.Down, false); page.OnAction(UiAction.Down, false);
            for (var i = 0; i < 3; i++) page.OnAction(UiAction.Left, false);
            Assert.Equal("watched", ((Card)page.LitElement!).Data.Badge);
        }

        [AvaloniaFact]
        public async Task Hold_ok_on_a_season_offers_play_request_and_the_watchlist()
        {
            var (page, rig) = await Simpsons();
            page.SeasonActions(page.Details!.Seasons[0]);
            var s1 = (ChoiceList)rig.Ctx.Nav.State.Overlay!.Params!;
            Assert.Equal(new[] { "Play season 1 from the start", "Add The Simpsons to watchlist" }, s1.Choices.Select(c => c.Label));
            rig.Ctx.Nav.CloseOverlay();

            page.OnAction(UiAction.Down, false);
            for (var i = 0; i < 3; i++) page.OnAction(UiAction.Right, false); // season 4: not streaming
            page.OnAction(UiAction.HoldOk, false);
            var s4 = (ChoiceList)rig.Ctx.Nav.State.Overlay!.Params!;
            Assert.Equal(new[] { "Request season 4", "Add The Simpsons to watchlist" }, s4.Choices.Select(c => c.Label));
        }
    }
}
