using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Mpv;
using Baird.Models;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.Services
{
    public class DisneyPlaybackEngineTests
    {
        private const string Home = "https://www.disneyplus.com/en-gb/home";
        internal static readonly string SeriesUrl = DisneySearchParser.EntityStreamUrl(DisneyEntityFixtures.SeriesId, isFilm: false);
        internal static readonly string EpisodePlayUrl = DisneySearchParser.PlayUrl(DisneyEntityFixtures.Episode1);

        /// <summary>A Disney+ browser that signs in, then plays an advancing video; polls by hand.</summary>
        internal static (FakeDisneyBrowser Browser, DisneyService Service, DisneyPlaybackEngine Engine) Engine(bool playsVideo = true)
        {
            var b = new FakeDisneyBrowser
            {
                LastState = FakeDisneyBrowser.State(Home, nav: true),
                EntityBody = DisneyEntityFixtures.Series(),
            };
            b.States.Enqueue(FakeDisneyBrowser.State(Home, nav: true)); // the sign-in check
            if (playsVideo)
                for (int t = 1; t <= 40; t++) b.States.Enqueue(FakeDisneyBrowser.State(EpisodePlayUrl, video: true, t: t));
            else
                b.LastState = FakeDisneyBrowser.State(EpisodePlayUrl); // the page never shows a playing video
            double position = 0;
            b.PollReply = () => FakeDisneyBrowser.Poll(position += 0.4);

            var service = new DisneyService(new BrowserHub(b), new DisneyCredentials("me@example.com", "pw"))
            {
                SignInTimeout = TimeSpan.FromSeconds(3),
                PollInterval = TimeSpan.FromMilliseconds(5)
            };
            var engine = new DisneyPlaybackEngine(service, () => new DisneySpikeViewModel(service)
            {
                UseBairdHud = true,
                BufferingTimeout = TimeSpan.FromMilliseconds(400)
            })
            {
                PollInterval = TimeSpan.FromHours(1), // tests poll by hand
            };
            return (b, service, engine);
        }

        [Theory]
        [InlineData("https://www.disneyplus.com/en-gb/play/abc", true)]
        [InlineData("https://www.disneyplus.com/en-gb/browse/entity-abc?baird=film", true)]
        [InlineData("https://www.netflix.com/watch/1", false)]
        [InlineData("http://tvh/stream/1", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Handles_OnlyDisneyPages(string? url, bool expected)
        {
            Assert.Equal(expected, DisneyPlaybackEngine.Handles(url));
        }

        [Fact]
        public async Task A_series_is_resolved_to_its_next_episode_then_played_and_the_runtime_is_the_duration()
        {
            var (b, service, engine) = Engine();
            var loaded = 0;
            double? duration = null;
            engine.FileLoaded += (_, _) => loaded++;
            engine.DurationChanged += (_, d) => duration = d;

            await engine.PlayAsync(SeriesUrl);

            Assert.Equal(PlaybackState.Playing, engine.State);
            Assert.Equal(1, loaded);
            Assert.Equal(2037, duration);
            Assert.Equal(2037, engine.DurationSeconds);
            Assert.Contains(EpisodePlayUrl, b.NavigatedUrls);
            Assert.Equal("Disney+", service.Hub.ActivePlayback);
            Assert.False(b.RevealWindowCalled); // Baird's player, not the raised browser
            Assert.False(b.StopCalled);
        }

        [Fact]
        public async Task A_play_url_is_played_as_it_is_with_an_unknown_length()
        {
            var (b, _, engine) = Engine();
            var durationEvents = 0;
            engine.DurationChanged += (_, _) => durationEvents++;

            await engine.PlayAsync(EpisodePlayUrl);

            Assert.Equal(PlaybackState.Playing, engine.State);
            Assert.Equal(0, b.FetchCalls); // no entity page was read
            Assert.Equal(0, engine.DurationSeconds);
            Assert.Equal(0, durationEvents);
        }

        [Fact]
        public async Task Loading_reports_the_steps_in_plain_words()
        {
            var (_, _, engine) = Engine();
            var texts = new List<string>();
            engine.StatusChanged += (_, _) => texts.Add(engine.StatusText);

            await engine.PlayAsync(SeriesUrl);

            Assert.Contains("Finding what to play…", texts);
            Assert.Contains("Loading the video…", texts);
            Assert.Equal("Playing.", engine.StatusText);
        }

        [Fact]
        public async Task Poll_reports_the_position_and_the_pause_state()
        {
            var (b, _, engine) = Engine();
            bool? paused = null;
            engine.PauseStateChanged += (_, p) => paused = p;
            await engine.PlayAsync(SeriesUrl);

            b.PollReply = () => FakeDisneyBrowser.Poll(23, paused: true, titleSeconds: 754);
            await engine.PollOnceAsync();

            Assert.Equal(754, engine.TimePositionSeconds);
            Assert.True(paused);
            Assert.Equal(PlaybackState.Paused, engine.State);
        }

        [Fact]
        public async Task Seek_clicks_Disneys_own_skip_button_and_pause_acts_on_the_video()
        {
            var (b, _, engine) = Engine();
            await engine.PlayAsync(SeriesUrl);

            engine.SeekRelative(10);
            engine.Pause();
            await engine.WhenCommandsDoneAsync();

            Assert.Single(b.SkipScripts);
            Assert.Contains("'Skip ahead 10 seconds', 1)", b.SkipScripts[0]);
            Assert.Single(b.ControlScripts);
            Assert.EndsWith("('Pause')", b.ControlScripts[0]);
            Assert.Equal(PlaybackState.Paused, engine.State);
        }

        [Theory]
        [InlineData(10, true, 1)]
        [InlineData(-10, false, 1)]
        [InlineData(30, true, 3)]
        [InlineData(-25, false, 3)]
        [InlineData(2, true, 1)]
        [InlineData(5000, true, 30)]
        public async Task A_seek_is_a_number_of_ten_second_skip_clicks_in_the_right_direction(double seconds, bool forward, int clicks)
        {
            var (b, _, engine) = Engine();
            await engine.PlayAsync(SeriesUrl);

            engine.SeekRelative(seconds);
            await engine.WhenCommandsDoneAsync();

            Assert.Contains($"'{(forward ? "Skip ahead 10 seconds" : "Skip back 10 seconds")}', {clicks})", b.SkipScripts.Single());
        }

        [Fact]
        public async Task A_seek_wakes_the_controls_first_because_the_skip_buttons_are_only_in_the_page_then()
        {
            var (b, _, engine) = Engine();
            await engine.PlayAsync(SeriesUrl);
            var before = b.MouseMoves;

            engine.SeekRelative(10);
            await engine.WhenCommandsDoneAsync();

            Assert.True(b.MouseMoves > before);
        }

        [Fact]
        public async Task Stop_returns_the_tab_to_the_idle_page_keeps_the_browser_and_frees_the_hub()
        {
            var (b, service, engine) = Engine();
            await engine.PlayAsync(SeriesUrl);

            await engine.StopAsync();

            Assert.Equal(PlaybackState.Idle, engine.State);
            Assert.Equal(DisneyService.IdleUrl, b.LastNavigatedUrl);
            Assert.False(b.StopCalled);
            Assert.Null(service.Hub.ActivePlayback);
        }

        [Fact]
        public async Task A_series_whose_page_has_nothing_to_play_fails_with_the_reason_and_frees_the_hub()
        {
            var (b, service, engine) = Engine();
            b.EntityBody = DisneyEntityFixtures.Series(withPlayback: false);
            string? reason = null;
            engine.Failed += (_, r) => reason = r;

            await engine.PlayAsync(SeriesUrl);

            Assert.Contains("Could not find what to play", reason);
            Assert.Equal(PlaybackState.Idle, engine.State);
            Assert.Null(service.Hub.ActivePlayback);
        }

        [Fact]
        public async Task A_video_that_never_plays_fails_with_the_flows_reason()
        {
            var (_, service, engine) = Engine(playsVideo: false);
            string? reason = null;
            var failures = 0;
            engine.Failed += (_, r) => { reason = r; failures++; };

            await engine.PlayAsync(SeriesUrl);

            Assert.Contains("No video playback observed", reason);
            Assert.Equal(1, failures);
            Assert.Equal(PlaybackState.Idle, engine.State);
            Assert.Null(service.Hub.ActivePlayback);
        }

        [Fact]
        public async Task Not_signed_in_and_no_credentials_fails_naming_the_config_keys()
        {
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/identity/login/enter-email", login: true) };
            var service = new DisneyService(new BrowserHub(b), null) { SignInTimeout = TimeSpan.FromSeconds(1), PollInterval = TimeSpan.FromMilliseconds(5) };
            var engine = new DisneyPlaybackEngine(service, () => new DisneySpikeViewModel(service) { UseBairdHud = true });
            string? reason = null;
            engine.Failed += (_, r) => reason = r;

            await engine.PlayAsync(SeriesUrl);

            Assert.Contains("DISNEY_USER", reason);
        }

        [Fact]
        public async Task Two_polls_without_contact_fail_visibly_and_stop()
        {
            var (b, _, engine) = Engine();
            string? reason = null;
            engine.Failed += (_, r) => reason = r;
            await engine.PlayAsync(SeriesUrl);

            b.PollReply = () => throw new InvalidOperationException("socket closed");
            await engine.PollOnceAsync();
            Assert.Null(reason); // one bad poll is tolerated
            await engine.PollOnceAsync();

            Assert.Contains("No contact with the Disney+ browser", reason);
            Assert.Equal(PlaybackState.Idle, engine.State);
        }

        [Fact]
        public async Task A_video_that_has_ended_raises_StreamEnded_once_and_stops()
        {
            var (b, _, engine) = Engine();
            var ended = 0;
            engine.StreamEnded += (_, _) => ended++;
            await engine.PlayAsync(SeriesUrl);

            b.PollReply = () => FakeDisneyBrowser.Poll(2037, ended: true);
            await engine.PollOnceAsync();
            await engine.PollOnceAsync(); // already idle: nothing more

            Assert.Equal(1, ended);
            Assert.Equal(PlaybackState.Idle, engine.State);
        }

        [Fact]
        public async Task A_page_that_lost_its_video_off_the_player_page_ends_playback_after_three_polls()
        {
            var (b, _, engine) = Engine();
            var ended = 0;
            engine.StreamEnded += (_, _) => ended++;
            await engine.PlayAsync(SeriesUrl);

            b.PollReply = () => FakeDisneyBrowser.Poll(0, found: false, url: "https://www.disneyplus.com/en-gb/home");
            await engine.PollOnceAsync();
            await engine.PollOnceAsync();
            Assert.Equal(0, ended);
            await engine.PollOnceAsync();

            Assert.Equal(1, ended);
        }

        [Fact]
        public async Task A_video_that_drops_for_a_moment_on_the_player_page_does_not_end_playback()
        {
            var (b, _, engine) = Engine();
            var ended = 0;
            engine.StreamEnded += (_, _) => ended++;
            await engine.PlayAsync(SeriesUrl);

            b.PollReply = () => FakeDisneyBrowser.Poll(0, found: false); // still on /play/{id}: Disney+ is switching titles
            for (var i = 0; i < 10; i++) await engine.PollOnceAsync();

            Assert.Equal(0, ended);
            Assert.Equal(PlaybackState.Playing, engine.State);
        }

        [Fact]
        public async Task A_quick_pause_then_resume_ends_playing_and_is_not_echoed_back_to_the_player()
        {
            var (b, _, engine) = Engine();
            var echoes = new List<bool>();
            engine.PauseStateChanged += (_, p) => echoes.Add(p);
            await engine.PlayAsync(SeriesUrl);

            engine.Pause();
            Assert.Equal(PlaybackState.Paused, engine.State); // at once, before the browser answered
            engine.Resume();
            await engine.WhenCommandsDoneAsync();

            Assert.Equal(PlaybackState.Playing, engine.State);
            Assert.Empty(echoes);
            Assert.Equal(2, b.ControlScripts.Count);
            Assert.EndsWith("('Pause')", b.ControlScripts[0]);
            Assert.EndsWith("('Play')", b.ControlScripts[1]);
        }

        [Fact]
        public async Task A_poll_that_shows_the_video_paused_is_still_reported_to_the_player()
        {
            var (b, _, engine) = Engine();
            var echoes = new List<bool>();
            engine.PauseStateChanged += (_, p) => echoes.Add(p);
            await engine.PlayAsync(SeriesUrl);

            b.PollReply = () => FakeDisneyBrowser.Poll(10, paused: true); // paused by something else
            await engine.PollOnceAsync();

            Assert.Equal(new[] { true }, echoes);
        }

        [Fact]
        public async Task A_play_waits_for_the_other_service_to_finish_stopping_before_it_takes_the_tab()
        {
            var (b, service, engine) = Engine();
            service.Hub.ActivePlayback = "Netflix"; // Netflix is still stopping
            var started = engine.PlayAsync(SeriesUrl);

            await Task.Delay(200);
            Assert.False(started.IsCompleted);
            Assert.Equal(0, b.FetchCalls); // nothing touched the page yet

            service.Hub.ActivePlayback = null; // Netflix's tab is back on its idle page
            await started;

            Assert.Equal(PlaybackState.Playing, engine.State);
        }

        [Fact]
        public async Task A_stop_before_the_video_plays_cancels_the_play_without_a_failure()
        {
            var (_, service, engine) = Engine();
            string? reason = null;
            engine.Failed += (_, r) => reason = r;
            var play = engine.PlayAsync(SeriesUrl);

            await engine.StopAsync();
            await play;

            Assert.Null(reason);
            Assert.Equal(PlaybackState.Idle, engine.State);
            Assert.Null(service.Hub.ActivePlayback);
        }
    }

    public class DisneyPlayerControlTests
    {
        [Fact]
        public void The_video_chosen_is_the_one_with_a_source_because_the_first_video_element_stays_empty()
        {
            Assert.Contains("x.readyState > 0 && x.currentSrc", DisneyPlayerControl.PollScript);
            Assert.Contains("x.readyState > 0 && x.currentSrc", DisneyPlayerControl.ControlScript(NetflixPlayerAction.Play));
        }

        [Fact]
        public void The_title_position_comes_from_the_controls_not_from_the_element_time()
        {
            var script = DisneyPlayerControl.PollScript;
            Assert.Contains("aria-valuemax", script);          // the exact length of the title
            Assert.Contains("time-remaining", script);         // the live countdown
            Assert.Contains("length - remaining", script);     // position = length - remaining
        }

        [Fact]
        public void Seeking_never_sets_the_elements_currentTime_because_a_native_seek_can_stall_the_player()
        {
            Assert.DoesNotContain("currentTime =", DisneyPlayerControl.SkipScript(true, 3));
            Assert.DoesNotContain("currentTime =", DisneyPlayerControl.ControlScript(NetflixPlayerAction.SeekBy));
        }

        [Theory]
        [InlineData(10, 1)]
        [InlineData(-10, 1)]
        [InlineData(14, 1)]
        [InlineData(16, 2)]
        [InlineData(-60, 6)]
        [InlineData(0, 1)]
        [InlineData(100000, 30)]
        public void Skip_clicks_are_ten_second_steps_at_least_one_at_most_thirty(double seconds, int expected)
        {
            Assert.Equal(expected, DisneyPlayerControl.SkipClicks(seconds));
        }

        [Fact]
        public void The_skip_script_clicks_the_button_with_Disneys_own_label_and_waits_between_clicks()
        {
            var forward = DisneyPlayerControl.SkipScript(true, 2);
            var back = DisneyPlayerControl.SkipScript(false, 2);
            Assert.Contains("'Skip ahead 10 seconds', 2)", forward);
            Assert.Contains("'Skip back 10 seconds', 2)", back);
            Assert.Contains("setTimeout(r, 300)", forward);
        }

        [Fact]
        public async Task Poll_reads_pause_end_and_the_title_position_and_length()
        {
            var b = new FakeDisneyBrowser { PollReply = () => FakeDisneyBrowser.Poll(23.4, paused: true, titleSeconds: 311, titleLength: 2444) };

            var status = await DisneyPlayerControl.PollAsync(b);

            Assert.True(status!.Found);
            Assert.Equal(23.4, status.CurrentTime);   // the element's time: not the title's
            Assert.Equal(311, status.TitleSeconds);
            Assert.Equal(2444, status.TitleLengthSeconds);
            Assert.True(status.Paused);
            Assert.False(status.Ended);
        }

        [Fact]
        public async Task Poll_moves_the_pointer_first_so_the_controls_are_in_the_page()
        {
            var b = new FakeDisneyBrowser { PollReply = () => FakeDisneyBrowser.Poll(1) };
            await DisneyPlayerControl.PollAsync(b);
            await DisneyPlayerControl.PollAsync(b);
            Assert.Equal(2, b.MouseMoves);
        }

        [Fact]
        public async Task Without_the_controls_the_title_position_and_length_are_unknown_not_zero()
        {
            var b = new FakeDisneyBrowser { PollReply = () => FakeDisneyBrowser.Poll(5) };
            var status = await DisneyPlayerControl.PollAsync(b);
            Assert.Null(status!.TitleSeconds);
            Assert.Null(status.TitleLengthSeconds);
        }

        [Fact]
        public async Task A_missing_answer_is_null()
        {
            var b = new FakeDisneyBrowser { PollReply = null };
            Assert.Null(await DisneyPlayerControl.PollAsync(b));
        }

        [Fact]
        public async Task Control_returns_the_accepted_path_for_pause_and_for_a_seek()
        {
            var b = new FakeDisneyBrowser();
            Assert.Equal("video", await DisneyPlayerControl.ControlAsync(b, NetflixPlayerAction.Pause, 0));
            Assert.Equal("buttons", await DisneyPlayerControl.ControlAsync(b, NetflixPlayerAction.SeekBy, 10));
        }
    }

    public class DisneyUrlTests
    {
        [Fact]
        public void An_entity_stream_url_round_trips_with_the_film_marker()
        {
            var film = DisneySearchParser.EntityStreamUrl("abc-123", isFilm: true);
            var series = DisneySearchParser.EntityStreamUrl("abc-123", isFilm: false);

            Assert.True(DisneySearchParser.TryParseEntityUrl(film, out var id1, out var isFilm1));
            Assert.Equal("abc-123", id1);
            Assert.True(isFilm1);
            Assert.True(DisneySearchParser.TryParseEntityUrl(series, out var id2, out var isFilm2));
            Assert.Equal("abc-123", id2);
            Assert.False(isFilm2);
        }

        [Theory]
        [InlineData("https://www.disneyplus.com/en-gb/play/abc")]
        [InlineData("https://www.disneyplus.com/en-gb/home")]
        [InlineData("https://www.netflix.com/browse/entity-abc")]
        [InlineData(null)]
        public void Other_urls_are_not_entity_urls(string? url)
        {
            Assert.False(DisneySearchParser.TryParseEntityUrl(url, out _, out _));
        }

        [Fact]
        public void A_film_search_result_carries_the_film_marker_and_a_series_does_not()
        {
            var film = DisneyProvider.ToMediaItem(new DisneySearchResult("F", "film-1", null, "movie"));
            var series = DisneyProvider.ToMediaItem(new DisneySearchResult("S", "series-1", null, "series"));

            Assert.EndsWith("/browse/entity-film-1?baird=film", film.StreamUrl);
            Assert.Equal("", series.StreamUrl);
            Assert.True(DisneyPlaybackEngine.Handles(film.StreamUrl));
        }
    }

    public class BrowserHubWaitTests
    {
        [Fact]
        public async Task Waiting_returns_at_once_when_nobody_else_plays()
        {
            var hub = new BrowserHub(new FakeDisneyBrowser());
            Assert.True(await hub.WaitForOtherPlaybackToEndAsync("Disney+", TimeSpan.FromSeconds(1)));
            hub.ActivePlayback = "Disney+"; // its own playback is not "other"
            Assert.True(await hub.WaitForOtherPlaybackToEndAsync("Disney+", TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public async Task Waiting_returns_when_the_other_service_clears_its_flag()
        {
            var hub = new BrowserHub(new FakeDisneyBrowser()) { ActivePlayback = "Netflix" };
            var wait = hub.WaitForOtherPlaybackToEndAsync("Disney+", TimeSpan.FromSeconds(5));
            await Task.Delay(120);
            Assert.False(wait.IsCompleted);
            hub.ActivePlayback = null;
            Assert.True(await wait);
        }

        [Fact]
        public async Task Waiting_gives_up_after_the_timeout_when_the_other_service_keeps_playing()
        {
            var hub = new BrowserHub(new FakeDisneyBrowser()) { ActivePlayback = "Netflix" };
            Assert.False(await hub.WaitForOtherPlaybackToEndAsync("Disney+", TimeSpan.FromMilliseconds(150)));
        }
    }

    public class DisneyFlowEngineModeTests
    {
        private const string Play = "https://www.disneyplus.com/en-gb/play/d830ec57-499e-498c-b6b3-2b85a18a5f51";

        private static (FakeDisneyBrowser, DisneyService) Setup()
        {
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true) };
            b.States.Enqueue(FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true));
            for (int t = 1; t <= 12; t++) b.States.Enqueue(FakeDisneyBrowser.State(Play, video: true, t: t));
            var service = new DisneyService(new BrowserHub(b), new DisneyCredentials("me@example.com", "pw"))
            {
                SignInTimeout = TimeSpan.FromSeconds(3),
                PollInterval = TimeSpan.FromMilliseconds(5)
            };
            return (b, service);
        }

        [Fact]
        public async Task In_engine_mode_the_page_style_is_injected_the_window_is_not_raised_and_Baird_is_told()
        {
            var (b, service) = Setup();
            var vm = new DisneySpikeViewModel(service) { UseBairdHud = true, BufferingTimeout = TimeSpan.FromSeconds(2) };
            var confirmed = 0;
            vm.PlaybackConfirmed += () => confirmed++;

            await vm.LaunchPlaybackFlowAsync();

            Assert.Equal(DisneySpikeState.Playing, vm.State);
            Assert.Equal(1, confirmed);
            Assert.True(b.InjectOverlayCalled);
            Assert.False(b.RevealWindowCalled);
        }

        [Fact]
        public async Task In_the_spike_tab_mode_the_window_is_raised_and_Baird_is_not_told()
        {
            var (b, service) = Setup();
            var vm = new DisneySpikeViewModel(service) { UseBairdHud = false, BufferingTimeout = TimeSpan.FromSeconds(2) };
            var confirmed = 0;
            vm.PlaybackConfirmed += () => confirmed++;

            await vm.LaunchPlaybackFlowAsync();

            Assert.Equal(DisneySpikeState.Playing, vm.State);
            Assert.Equal(0, confirmed);
            Assert.True(b.RevealWindowCalled);
        }

        [Fact]
        public async Task The_playing_flag_is_cleared_only_after_the_tab_is_back_on_the_idle_page()
        {
            var (b, service) = Setup();
            var vm = new DisneySpikeViewModel(service) { UseBairdHud = true, BufferingTimeout = TimeSpan.FromSeconds(2) };
            await vm.LaunchPlaybackFlowAsync();
            Assert.Equal("Disney+", service.Hub.ActivePlayback);

            await vm.StopPlaybackAsync();

            Assert.Equal(DisneyService.IdleUrl, b.LastNavigatedUrl);
            Assert.Null(service.Hub.ActivePlayback);
        }

        [Fact]
        public async Task A_disposed_flow_no_longer_reacts_to_the_browser_or_the_service()
        {
            var (b, service) = Setup();
            var vm = new DisneySpikeViewModel(service) { UseBairdHud = true, BufferingTimeout = TimeSpan.FromSeconds(2) };
            await vm.LaunchPlaybackFlowAsync();
            var logLines = vm.LogEntries.Count;
            vm.Dispose();

            b.RaisePageSignal("{\"type\":\"exit\"}"); // the next flow's Back must not stop this old one

            Assert.Equal(DisneySpikeState.Playing, vm.State);
            Assert.False(b.NavigatedUrls.Contains(DisneyService.IdleUrl) && vm.State == DisneySpikeState.Stopped);
            Assert.Equal(logLines, vm.LogEntries.Count);
        }
    }

    public class StreamingProvidersWhilePlayingTests
    {
        private static (BrowserHub Hub, NetflixProvider Netflix, DisneyProvider Disney) Setup()
        {
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true) };
            var hub = new BrowserHub(b);
            var netflix = new NetflixProvider(new NetflixSession(b, new NetflixCredentials("a@b.c", "pw"), hub: hub));
            var disney = new DisneyProvider(new DisneyService(hub, new DisneyCredentials("a@b.c", "pw")));
            return (hub, netflix, disney);
        }

        [Fact]
        public async Task Netflix_search_returns_nothing_not_a_failure_while_Disney_plays()
        {
            var (hub, netflix, _) = Setup();
            hub.ActivePlayback = "Disney+";
            Assert.Empty(await netflix.SearchAsync("one piece"));
        }

        [Fact]
        public async Task Disney_search_returns_nothing_not_a_failure_while_Netflix_plays()
        {
            var (hub, _, disney) = Setup();
            hub.ActivePlayback = "Netflix";
            Assert.Empty(await disney.SearchAsync("mandalorian"));
        }

        [Fact]
        public async Task Disney_search_returns_nothing_while_Disney_itself_plays()
        {
            var (hub, _, disney) = Setup();
            hub.ActivePlayback = "Disney+";
            Assert.Empty(await disney.SearchAsync("mandalorian"));
        }
    }
}

namespace Baird.Tests.Services
{
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Baird.Models;
    using Baird.Mpv;
    using Baird.Services;
    using Baird.ViewModels;
    using Xunit;

    public class DisneyEpisodeGuideTests
    {
        [Fact]
        public void The_guide_lists_seasons_and_episodes_with_numbers_titles_runtimes_and_artwork()
        {
            var guide = DisneySearchParser.ParseEpisodeGuide(200, DisneyEntityFixtures.TwoSeasons(), out var failure)!;

            Assert.Null(failure);
            Assert.Equal("The Mandalorian", guide.SeriesTitle);
            Assert.Equal(new[] { "Season 1", "Season 2" }, guide.Seasons.Select(s => s.Name));
            Assert.Equal(new[] { 1, 2 }, guide.Seasons.Select(s => s.Number));
            var first = guide.Seasons[0].Episodes[0];
            Assert.Equal(DisneyEntityFixtures.Episode1, first.Id);
            Assert.Equal("Chapter 1: The Mandalorian", first.Title);
            Assert.Equal((1, 1), (first.Season, first.Number));
            Assert.Equal(2037, first.RuntimeSeconds);
            Assert.Equal("https://www.disneyplus.com/en-gb/play/" + DisneyEntityFixtures.Episode1, first.PlayUrl);
            Assert.Equal(0, guide.Seasons[0].Episodes[1].RuntimeSeconds); // Disney+ gave no length for this one
            Assert.Equal(3, guide.AllEpisodes.Count());
        }

        [Fact]
        public void A_film_page_has_no_episodes_which_is_an_empty_guide_not_a_failure()
        {
            var guide = DisneySearchParser.ParseEpisodeGuide(200, DisneyEntityFixtures.Film("film-1"), out var failure);
            Assert.Null(failure);
            Assert.Empty(guide!.Seasons);
        }

        [Theory]
        [InlineData(500, "{}", "HTTP 500")]
        [InlineData(200, "", "no reply")]
        [InlineData(200, "<html>", "BAD_JSON")]
        [InlineData(200, "{\"data\":{}}", "no data.page")]
        public void Errors_are_reported(int status, string body, string expected)
        {
            Assert.Null(DisneySearchParser.ParseEpisodeGuide(status, body, out var failure));
            Assert.Contains(expected, failure);
        }

        [Theory]
        [InlineData("https://www.disneyplus.com/en-gb/play/abc-123", true, "abc-123")]
        [InlineData("https://www.disneyplus.com/en-gb/play/abc-123?x=1", true, "abc-123")]
        [InlineData("https://www.disneyplus.com/en-gb/browse/entity-abc", false, "")]
        [InlineData("https://www.netflix.com/watch/1", false, "")]
        [InlineData(null, false, "")]
        public void A_play_id_is_read_from_a_play_url(string? url, bool ok, string expectedId)
        {
            Assert.Equal(ok, DisneySearchParser.TryParsePlayId(url, out var id));
            Assert.Equal(expectedId, id);
        }
    }

    public class DisneyChildrenTests
    {
        private static (FakeDisneyBrowser Browser, DisneyService Service, DisneyProvider Provider) Setup(string body)
        {
            var b = new FakeDisneyBrowser { LastState = FakeDisneyBrowser.State("https://www.disneyplus.com/en-gb/home", nav: true), EntityBody = body };
            var service = new DisneyService(new BrowserHub(b), new DisneyCredentials("me@example.com", "pw"))
            {
                SignInTimeout = TimeSpan.FromSeconds(3),
                PollInterval = TimeSpan.FromMilliseconds(5)
            };
            return (b, service, new DisneyProvider(service));
        }

        [Fact]
        public async Task A_series_with_several_seasons_gives_season_folders()
        {
            var (_, _, provider) = Setup(DisneyEntityFixtures.TwoSeasons());

            var children = (await provider.GetChildrenAsync("disney:series-1")).ToList();

            Assert.Equal(new[] { "Season 1", "Season 2" }, children.Select(c => c.Name));
            Assert.All(children, c => { Assert.Equal(MediaType.Brand, c.Type); Assert.Equal("", c.StreamUrl); Assert.Equal("Disney+", c.Source); });
            Assert.Equal(new[] { "disney:series-1|1", "disney:series-1|2" }, children.Select(c => c.Id));
            Assert.Equal("2 Episodes", children[0].Details);
            Assert.Equal("1 Episode", children[1].Details);
        }

        [Fact]
        public async Task A_season_gives_its_episodes_as_playable_items_with_play_urls_and_lengths()
        {
            var (_, _, provider) = Setup(DisneyEntityFixtures.TwoSeasons());

            var episodes = (await provider.GetChildrenAsync("disney:series-1|1")).ToList();

            Assert.Equal(2, episodes.Count);
            var e1 = episodes[0];
            Assert.Equal("disney:ep:" + DisneyEntityFixtures.Episode1, e1.Id);
            Assert.DoesNotContain("|", e1.Id); // Baird's season and continuous-play code splits ids on '|'
            Assert.Equal("Chapter 1: The Mandalorian", e1.Name);
            Assert.Equal("S1:E1", e1.Details);
            Assert.Equal(MediaType.Video, e1.Type);
            Assert.Equal("https://www.disneyplus.com/en-gb/play/" + DisneyEntityFixtures.Episode1, e1.StreamUrl);
            Assert.Equal(TimeSpan.FromSeconds(2037), e1.Duration);
            Assert.Equal(TimeSpan.Zero, episodes[1].Duration);
            Assert.True(DisneyPlaybackEngine.Handles(e1.StreamUrl));
        }

        [Fact]
        public async Task A_series_with_one_season_gives_the_episodes_directly()
        {
            var (_, _, provider) = Setup(DisneyEntityFixtures.Series());

            var children = (await provider.GetChildrenAsync("disney:series-1")).ToList();

            Assert.Equal(2, children.Count);
            Assert.All(children, c => Assert.Equal(MediaType.Video, c.Type));
        }

        [Theory]
        [InlineData("jellyfin:abc")]
        [InlineData("netflix:123")]
        [InlineData("disney:ep:8b4a1f96")]
        [InlineData("")]
        public async Task Other_providers_ids_and_episodes_have_no_children(string id)
        {
            var (b, _, provider) = Setup(DisneyEntityFixtures.TwoSeasons());
            Assert.Empty(await provider.GetChildrenAsync(id));
            Assert.Equal(0, b.FetchCalls);
        }

        [Fact]
        public async Task A_film_has_no_children()
        {
            var (_, _, provider) = Setup(DisneyEntityFixtures.Film("film-1"));
            Assert.Empty(await provider.GetChildrenAsync("disney:film-1"));
        }

        [Fact]
        public async Task A_Disney_failure_throws_so_the_data_service_logs_it_and_continues()
        {
            var (b, _, provider) = Setup("{}");
            b.FetchStatuses.Enqueue(500);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetChildrenAsync("disney:series-1"));

            Assert.Contains("Disney+ episodes failed", ex.Message);
        }

        [Fact]
        public async Task The_episode_list_is_reused_until_it_is_old()
        {
            var (b, service, provider) = Setup(DisneyEntityFixtures.TwoSeasons());

            await provider.GetChildrenAsync("disney:series-1");
            await provider.GetChildrenAsync("disney:series-1|1");
            Assert.Equal(1, b.FetchCalls);

            service.GuideLifetime = TimeSpan.Zero;
            await provider.GetChildrenAsync("disney:series-1|2");
            Assert.Equal(2, b.FetchCalls);
        }

        [Fact]
        public async Task Reading_the_list_teaches_the_service_every_episodes_length()
        {
            var (_, service, provider) = Setup(DisneyEntityFixtures.TwoSeasons());
            Assert.Equal(0, service.KnownRuntimeSeconds(DisneyEntityFixtures.Episode3));

            await provider.GetChildrenAsync("disney:series-1");

            Assert.Equal(2037, service.KnownRuntimeSeconds(DisneyEntityFixtures.Episode1));
            Assert.Equal(2700, service.KnownRuntimeSeconds(DisneyEntityFixtures.Episode3));
            Assert.Equal(0, service.KnownRuntimeSeconds(DisneyEntityFixtures.Episode2)); // Disney+ gave none
        }

        [Fact]
        public async Task Resolving_a_series_also_teaches_the_lengths_of_its_other_episodes()
        {
            var (_, service, _) = Setup(DisneyEntityFixtures.Series());

            await service.ResolvePlayTargetAsync("series-1", false, 0, CancellationToken.None);

            Assert.Equal(1860, service.KnownRuntimeSeconds(DisneyEntityFixtures.Episode2));
        }
    }

    public class DisneyEpisodeLengthTests
    {
        [Fact]
        public async Task An_episode_played_by_its_url_after_the_list_was_read_has_its_length()
        {
            var (b, service, engine) = DisneyPlaybackEngineTests.Engine();
            service.RememberRuntime(DisneyEntityFixtures.Episode1, 2037);
            double? duration = null;
            engine.DurationChanged += (_, d) => duration = d;

            await engine.PlayAsync(DisneyPlaybackEngineTests.EpisodePlayUrl);

            Assert.Equal(PlaybackState.Playing, engine.State);
            Assert.Equal(2037, duration);
            Assert.Equal(2037, engine.DurationSeconds);
            Assert.Equal(0, b.FetchCalls); // no page was read: the length was already known
        }

        [Fact]
        public async Task An_episode_whose_length_is_unknown_plays_with_no_length_not_a_wrong_one()
        {
            var (_, _, engine) = DisneyPlaybackEngineTests.Engine();
            await engine.PlayAsync(DisneyPlaybackEngineTests.EpisodePlayUrl);
            Assert.Equal(0, engine.DurationSeconds);
        }

        [Fact]
        public async Task When_Disney_moves_to_another_episode_the_title_is_over_and_Baird_plays_the_next_one_itself()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            var ended = 0;
            engine.StreamEnded += (_, _) => ended++;
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);
            await engine.PollOnceAsync();
            Assert.Equal(0, ended); // same title: carry on

            b.PollReply = () => FakeDisneyBrowser.Poll(3, url: "https://www.disneyplus.com/en-gb/play/" + DisneyEntityFixtures.Episode2);
            await engine.PollOnceAsync();

            Assert.Equal(1, ended);
            Assert.Equal(PlaybackState.Idle, engine.State);
        }

        [Fact]
        public async Task Another_episode_does_not_leave_the_wrong_length_behind_for_the_next_play()
        {
            var (_, service, engine) = DisneyPlaybackEngineTests.Engine();
            service.RememberRuntime(DisneyEntityFixtures.Episode1, 2037);
            await engine.PlayAsync(DisneyPlaybackEngineTests.EpisodePlayUrl);
            Assert.Equal(2037, engine.DurationSeconds);
            await engine.StopAsync();

            // a second play of an episode with no known length must not inherit the first one's
            var (_, _, engine2) = DisneyPlaybackEngineTests.Engine();
            await engine2.PlayAsync(DisneyPlaybackEngineTests.EpisodePlayUrl);
            Assert.Equal(0, engine2.DurationSeconds);
        }
    }
}

namespace Baird.Tests.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Baird.Mpv;
    using Baird.Services;
    using Xunit;

    public class DisneyPromptTests
    {
        [Theory]
        [InlineData("SKIP INTRO", "Skip intro")]
        [InlineData("Skip Recap", "Skip recap")]
        [InlineData("  next episode ", "Next episode")]
        public void A_label_shows_in_sentence_case(string label, string expected)
        {
            Assert.Equal(expected, DisneyPlayerControl.Normalize(label));
        }

        [Fact]
        public async Task The_poll_carries_the_prompt_in_sentence_case_and_none_when_absent()
        {
            var b = new FakeDisneyBrowser { PollReply = () => FakeDisneyBrowser.Poll(5, prompt: "SKIP INTRO") };
            Assert.Equal("Skip intro", (await DisneyPlayerControl.PollAsync(b))!.Prompt);

            b.PollReply = () => FakeDisneyBrowser.Poll(5);
            Assert.Null((await DisneyPlayerControl.PollAsync(b))!.Prompt);
        }

        [Fact]
        public void Only_known_questions_count_as_prompts_and_the_transport_buttons_do_not()
        {
            var script = DisneyPlayerControl.PollScript;
            Assert.Contains("^skip (intro|recap|credits|opening|previously)$", script);
            Assert.DoesNotContain("Skip ahead", script); // the permanent 10-second buttons are not questions
            Assert.Contains("e.shadowRoot", script);      // the player UI is in shadow DOM
            Assert.Contains("END-CARD-OVERLAY", script);  // the end card is the "next episode" prompt
        }

        [Fact]
        public void The_page_style_makes_the_controls_transparent_not_absent_so_prompts_in_them_stay_clickable()
        {
            var style = CdpBrowserService.LoadDisneyPageStyleScript();
            Assert.Contains("MAIN-APP-CONTROLS-OVERLAY", style);
            Assert.Contains("'opacity', '0'", style);
            Assert.DoesNotContain("'display', 'none'", style);
        }

        [Fact]
        public async Task The_engine_reports_the_prompt_while_it_is_on_screen_and_clears_it_when_it_goes()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            var changes = new List<string?>();
            engine.PromptChanged += (_, _) => changes.Add(engine.PromptText);
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);
            Assert.Null(engine.PromptText);

            b.PollReply = () => FakeDisneyBrowser.Poll(30, prompt: "SKIP INTRO");
            await engine.PollOnceAsync();
            await engine.PollOnceAsync(); // unchanged: no second event
            b.PollReply = () => FakeDisneyBrowser.Poll(40);
            await engine.PollOnceAsync();

            Assert.Equal(new string?[] { "Skip intro", null }, changes);
        }

        [Fact]
        public async Task OK_answers_the_prompt_by_clicking_it_and_the_prompt_goes_at_once()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);
            b.PollReply = () => FakeDisneyBrowser.Poll(30, prompt: "SKIP INTRO");
            await engine.PollOnceAsync();

            Assert.True(engine.ActivatePrompt());
            await engine.WhenCommandsDoneAsync();

            Assert.Equal(1, b.PromptClicks);
            Assert.Null(engine.PromptText);
        }

        [Fact]
        public async Task Answering_when_there_is_no_prompt_does_nothing()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);

            Assert.False(engine.ActivatePrompt());
            await engine.WhenCommandsDoneAsync();

            Assert.Equal(0, b.PromptClicks);
        }

        [Fact]
        public async Task A_prompt_that_vanished_before_the_click_is_not_an_error()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);
            b.PollReply = () => FakeDisneyBrowser.Poll(30, prompt: "SKIP RECAP");
            await engine.PollOnceAsync();
            b.PromptOnScreen = false;

            Assert.True(engine.ActivatePrompt());
            await engine.WhenCommandsDoneAsync();

            Assert.Null(engine.PromptText);
            Assert.Equal(PlaybackState.Playing, engine.State);
        }

        [Fact]
        public async Task A_stop_clears_the_prompt()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);
            b.PollReply = () => FakeDisneyBrowser.Poll(30, prompt: "SKIP RECAP");
            await engine.PollOnceAsync();
            Assert.Equal("Skip recap", engine.PromptText);

            await engine.StopAsync();

            Assert.Null(engine.PromptText);
        }
    }
}

namespace Baird.Tests.Services
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Baird.Mpv;
    using Baird.Services;
    using Xunit;

    public class DisneyTitleTimelineTests
    {
        [Fact]
        public async Task The_position_is_the_titles_position_from_the_controls_not_the_elements_time()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);

            // measured on the Pi: the element read 23 s while the title was at 311 s
            b.PollReply = () => FakeDisneyBrowser.Poll(23.4, titleSeconds: 311, titleLength: 2444);
            await engine.PollOnceAsync();

            Assert.Equal(311, engine.TimePositionSeconds);
        }

        [Fact]
        public async Task The_length_from_the_controls_is_exact_replaces_the_API_length_and_is_remembered_for_that_title()
        {
            var (b, service, engine) = DisneyPlaybackEngineTests.Engine();
            double? announced = null;
            engine.DurationChanged += (_, d) => announced = d;
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);
            Assert.Equal(2037, engine.DurationSeconds); // from the API at first

            b.PollReply = () => FakeDisneyBrowser.Poll(5, titleSeconds: 10, titleLength: 2444);
            await engine.PollOnceAsync();

            Assert.Equal(2444, engine.DurationSeconds);
            Assert.Equal(2444, announced);
            Assert.Equal(2444, service.KnownRuntimeSeconds(DisneyEntityFixtures.Episode1));
        }

        [Fact]
        public async Task A_length_that_has_not_changed_is_not_announced_again()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            var announcements = 0;
            await engine.PlayAsync(DisneyPlaybackEngineTests.EpisodePlayUrl);
            engine.DurationChanged += (_, _) => announcements++;

            b.PollReply = () => FakeDisneyBrowser.Poll(5, titleSeconds: 10, titleLength: 2444);
            await engine.PollOnceAsync();
            await engine.PollOnceAsync();
            await engine.PollOnceAsync();

            Assert.Equal(1, announcements);
        }

        [Fact]
        public async Task While_the_controls_are_not_in_the_page_the_position_keeps_counting_but_not_when_paused()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);
            b.PollReply = () => FakeDisneyBrowser.Poll(5, titleSeconds: 100, titleLength: 2444);
            await engine.PollOnceAsync();
            Assert.Equal(100, engine.TimePositionSeconds);

            await Task.Delay(300);
            b.PollReply = () => FakeDisneyBrowser.Poll(6); // no controls, so no title position
            await engine.PollOnceAsync();
            Assert.InRange(engine.TimePositionSeconds, 100.2, 101.5);

            var heldAt = engine.TimePositionSeconds;
            await Task.Delay(200);
            b.PollReply = () => FakeDisneyBrowser.Poll(6, paused: true);
            await engine.PollOnceAsync();
            Assert.Equal(heldAt, engine.TimePositionSeconds, 1); // paused: the position holds
        }

        [Fact]
        public async Task A_seek_moves_the_shown_position_at_once_and_the_next_poll_corrects_it()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);
            b.PollReply = () => FakeDisneyBrowser.Poll(5, titleSeconds: 100, titleLength: 2444);
            await engine.PollOnceAsync();

            engine.SeekRelative(-10);
            Assert.Equal(90, engine.TimePositionSeconds);
            await engine.WhenCommandsDoneAsync();

            b.PollReply = () => FakeDisneyBrowser.Poll(23, titleSeconds: 93, titleLength: 2444);
            await engine.PollOnceAsync();
            Assert.Equal(93, engine.TimePositionSeconds);
        }

        [Fact]
        public async Task Disneys_end_card_is_the_Next_episode_prompt_and_is_closed_once_so_Baird_plays_what_comes_next()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);
            b.PollReply = () => FakeDisneyBrowser.Poll(1400, prompt: "Next episode", titleSeconds: 1400, titleLength: 1435);

            await engine.PollOnceAsync();
            await engine.PollOnceAsync();
            await engine.PollOnceAsync();
            await engine.WhenCommandsDoneAsync();

            Assert.Equal("Next episode", engine.PromptText);
            Assert.Equal(1, b.EndCardCloses); // once per title, not once per poll
        }

        [Fact]
        public async Task The_end_card_is_closed_again_for_the_next_title()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);
            b.PollReply = () => FakeDisneyBrowser.Poll(1400, prompt: "Next episode");
            await engine.PollOnceAsync();
            await engine.WhenCommandsDoneAsync();
            Assert.Equal(1, b.EndCardCloses);
            await engine.StopAsync();

            b.States.Clear();
            b.States.Enqueue(FakeDisneyBrowser.State(DisneyPlaybackEngineTests.EpisodePlayUrl, video: true, t: 1));
            for (int t = 2; t <= 30; t++) b.States.Enqueue(FakeDisneyBrowser.State(DisneyPlaybackEngineTests.EpisodePlayUrl, video: true, t: t));
            await engine.PlayAsync(DisneyPlaybackEngineTests.EpisodePlayUrl);
            b.PollReply = () => FakeDisneyBrowser.Poll(1400, prompt: "Next episode");
            await engine.PollOnceAsync();
            await engine.WhenCommandsDoneAsync();

            Assert.Equal(2, b.EndCardCloses);
        }

        [Fact]
        public async Task Answering_Next_episode_ends_the_title_at_once_so_Baird_plays_the_next_one()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            var ended = 0;
            engine.StreamEnded += (_, _) => ended++;
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);
            b.PollReply = () => FakeDisneyBrowser.Poll(1400, prompt: "Next episode");
            await engine.PollOnceAsync();

            Assert.True(engine.ActivatePrompt());
            await Task.Delay(100);

            Assert.Equal(1, ended);
            Assert.Equal(PlaybackState.Idle, engine.State);
            Assert.Equal(0, b.PromptClicks); // nothing was clicked on Disney+'s page
            Assert.Null(engine.PromptText);
        }

        [Fact]
        public async Task Answering_Skip_intro_clicks_the_button_and_nothing_else_happens()
        {
            var (b, _, engine) = DisneyPlaybackEngineTests.Engine();
            var ended = 0;
            engine.StreamEnded += (_, _) => ended++;
            await engine.PlayAsync(DisneyPlaybackEngineTests.SeriesUrl);
            b.PollReply = () => FakeDisneyBrowser.Poll(25, prompt: "SKIP INTRO", titleSeconds: 12, titleLength: 1435);
            await engine.PollOnceAsync();

            Assert.True(engine.ActivatePrompt());
            await engine.WhenCommandsDoneAsync();

            Assert.Equal(1, b.PromptClicks);
            Assert.Equal(0, ended);
            Assert.Equal(0, b.EndCardCloses);
            Assert.Equal(PlaybackState.Playing, engine.State);
        }
    }
}
