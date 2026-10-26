using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Baird.Controls;
using Baird.Mpv;
using Baird.Playback;
using Baird.Services;
using Baird.Tests.Services;
using Xunit;

namespace Baird.Tests.Controls
{
    public class VideoPlayerEngineTests
    {
        private static readonly string WatchUrl = NetflixProvider.WatchUrl("80107103");
        private static readonly string[] StepTexts = { "Starting Netflix…", "Signing in to Netflix…", "Loading the video…" };

        private sealed class FakeMpv : IPlaybackEngine
        {
            public List<string> Played { get; } = new();
            public PlaybackState State { get; private set; } = PlaybackState.Idle;
            public double TimePositionSeconds => 0;
            public double DurationSeconds => 0;
            public event EventHandler? FileLoaded { add { } remove { } }
            public event EventHandler? StreamEnded { add { } remove { } }
            public event EventHandler<bool>? PauseStateChanged { add { } remove { } }
            public event EventHandler<double>? DurationChanged { add { } remove { } }
            public void Play(string url, double? startSeconds = null) { Played.Add(url); State = PlaybackState.Loading; }
            public void Pause() { }
            public void Resume() { }
            public void Seek(double seconds) { }
            public void SeekRelative(double seconds) { }
            public void SetSubtitle(bool enabled) { }
            public void Stop() => State = PlaybackState.Idle;
        }

        private static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 3000)
        {
            for (var waited = 0; waited < timeoutMs; waited += 10)
            {
                Dispatcher.UIThread.RunJobs();
                if (condition()) return true;
                await Task.Delay(10);
            }
            Dispatcher.UIThread.RunJobs();
            return condition();
        }

        private static void Press(VideoPlayer player, Key key) =>
            player.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

        [AvaloniaFact]
        public async Task Channel4_not_signed_in_offers_OK_to_sign_in_and_plays_once_the_viewer_has()
        {
            var (b, _, engine) = Channel4PlaybackEngineTests.Engine();
            const string episode = Channel4PlaybackEngineTests.Episode;
            b.States.Enqueue(FakeChannel4Browser.State(episode, auth: false, signInPrompt: true));
            b.LastState = FakeChannel4Browser.State("https://www.channel4.com/sign-in-or-register?redirectUrl=x", email: true);
            var mpv = new FakeMpv();
            var player = new VideoPlayer(mpv);
            player.AddBrowserEngine(engine);

            player.Source = episode;
            Assert.True(await WaitUntil(() => player.IsShowingFailure));
            Assert.Contains("Press OK to sign in to Channel 4, or Back to return.", player.LoadingText);

            // The viewer signs in on Channel 4's page; then the page plays.
            b.States.Enqueue(FakeChannel4Browser.State("https://www.channel4.com/programmes/derry-girls", auth: true));
            Channel4PlaybackEngineTests.Plays(b, episode);
            Press(player, Key.Enter);

            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading, 5000), player.LoadingText);
            Assert.True(b.RevealWindowCalled);
            Assert.Empty(mpv.Played);
        }

        [AvaloniaFact]
        public async Task After_a_cancelled_sign_in_OK_counts_again_only_once_its_release_was_seen()
        {
            var (b, _, engine) = Channel4PlaybackEngineTests.Engine();
            const string episode = Channel4PlaybackEngineTests.Episode;
            b.States.Enqueue(FakeChannel4Browser.State(episode, auth: false, signInPrompt: true));
            b.LastState = FakeChannel4Browser.State("https://www.channel4.com/sign-in-or-register?redirectUrl=x", email: true, cancelled: true);
            var player = new VideoPlayer(new FakeMpv());
            player.AddBrowserEngine(engine);
            int SignInPages() => b.NavigatedUrls.Count(u => u.StartsWith(Channel4Service.SignInUrl, StringComparison.Ordinal));

            player.Source = episode;
            Assert.True(await WaitUntil(() => player.IsShowingFailure));
            Press(player, Key.Enter); // its release goes to Channel 4's page, not to Baird
            Assert.True(await WaitUntil(() => SignInPages() == 1 && player.IsShowingFailure && player.LoadingText.Contains("cancelled"), 5000));

            Press(player, Key.Enter); // the display server repeating the "held" key
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(100);
            Assert.Equal(1, SignInPages());

            player.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Enter });
            Press(player, Key.Enter); // a real new press
            Assert.True(await WaitUntil(() => SignInPages() == 2, 5000));
        }

        [AvaloniaFact]
        public async Task OK_on_a_failure_that_offers_nothing_does_nothing()
        {
            var (b, _, engine) = Channel4PlaybackEngineTests.Engine();
            b.LastState = FakeChannel4Browser.State(Channel4PlaybackEngineTests.Episode, auth: true, video: true, t: 0, text: "Something went wrong");
            var player = new VideoPlayer(new FakeMpv());
            player.AddBrowserEngine(engine);

            player.Source = Channel4PlaybackEngineTests.Episode;
            Assert.True(await WaitUntil(() => player.IsShowingFailure, 5000));
            Press(player, Key.Enter);
            Dispatcher.UIThread.RunJobs();

            Assert.True(player.IsShowingFailure);
            Assert.EndsWith("Press Back to return.", player.LoadingText);
            Assert.False(b.RevealWindowCalled);
        }

        [AvaloniaFact]
        public async Task NetflixUrl_UsesNetflixEngine_AndShowsBrowserOnceLoaded()
        {
            var (_, _, engine) = NetflixPlaybackEngineTests.Engine();
            var mpv = new FakeMpv();
            var player = new VideoPlayer(mpv) { NetflixEngine = engine };

            player.Source = WatchUrl;

            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));
            Assert.Empty(mpv.Played);
        }

        [AvaloniaFact]
        public async Task NetflixLoading_ShowsStepText()
        {
            var (_, _, engine) = NetflixPlaybackEngineTests.Engine();
            var player = new VideoPlayer(new FakeMpv()) { NetflixEngine = engine };
            var seen = new List<string>();
            player.PropertyChanged += (_, e) => { if (e.Property == VideoPlayer.LoadingTextProperty) seen.Add(player.LoadingText); };

            player.Source = WatchUrl;

            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));
            Assert.Contains(seen, t => Array.IndexOf(StepTexts, t) >= 0);
        }

        [AvaloniaFact]
        public async Task NetflixVerificationCode_IsTypedWithTheNumberKeys_AndOKSendsIt()
        {
            var (svc, _, engine) = NetflixPlaybackEngineTests.Engine();
            svc.AuthStatusToReturn = new AuthPageStatus("https://www.netflix.com/verify", "Verify", false, true, false, 0);
            var player = new VideoPlayer(new FakeMpv()) { NetflixEngine = engine };
            var searches = new List<string>();
            player.SearchRequested += (_, d) => searches.Add(d);

            player.Source = WatchUrl;
            Assert.True(await WaitUntil(() => player.IsEnteringCode), player.LoadingText);
            Assert.Contains("verification code", player.LoadingText);
            Assert.Contains("Code: ______", player.LoadingText);
            Assert.False(player.IsShowingFailure);

            Press(player, Key.Enter); // nothing typed yet: nothing is sent
            Press(player, Key.D1);
            Press(player, Key.NumPad2);
            Press(player, Key.D3);
            Press(player, Key.Left); // deletes the 3
            Press(player, Key.D4);
            Assert.Contains("Code: 124", player.LoadingText);
            Assert.Empty(searches); // the digits are the code, not a channel number
            Assert.False(svc.SubmitMfaCalled);

            svc.AuthStatusToReturn = new AuthPageStatus(WatchUrl, "t", false, false, false, 0); // Netflix accepts it
            Press(player, Key.Enter);

            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading, 5000), player.LoadingText);
            Assert.Equal("124", svc.LastSubmittedPin);
            Assert.False(player.IsEnteringCode);
        }

        [AvaloniaFact]
        public async Task NetflixFailure_ShowsTheReasonInTheLoadingOverlay()
        {
            var (_, _, engine) = NetflixPlaybackEngineTests.Engine(video: () => null);
            var player = new VideoPlayer(new FakeMpv()) { NetflixEngine = engine };

            player.Source = WatchUrl;

            Assert.True(await WaitUntil(() => player.LoadingText.EndsWith("Press Back to return.")));
            Assert.True(player.IsLoading);
            Assert.False(player.IsShowingBrowser);
        }

        [AvaloniaFact]
        public async Task PlayAfterNetflix_StopsNetflixAndUsesMpv()
        {
            var (svc, session, engine) = NetflixPlaybackEngineTests.Engine();
            var mpv = new FakeMpv();
            var player = new VideoPlayer(mpv) { NetflixEngine = engine };
            player.Source = WatchUrl;
            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));

            player.Source = "http://tvh/stream/1";

            Assert.True(await WaitUntil(() => mpv.Played.Contains("http://tvh/stream/1") && !session.IsPlaybackActive));
            Assert.False(player.IsShowingBrowser);
            Assert.Equal(NetflixSession.IdleUrl, svc.LastNavigatedUrl);
        }

        [AvaloniaFact]
        public async Task NetflixPlay_DoesNotUseMpvLoadTimeout()
        {
            var (_, _, engine) = NetflixPlaybackEngineTests.Engine(video: () => null); // stays loading ~300 ms, then fails
            var mpv = new FakeMpv();
            var player = new VideoPlayer(mpv) { NetflixEngine = engine, LoadTimeout = TimeSpan.FromMilliseconds(20) };

            player.Source = WatchUrl;
            await WaitUntil(() => false, 200);

            Assert.Empty(mpv.Played);
        }

        [AvaloniaFact]
        public async Task KeysOverNetflix_GoToNetflixEngine()
        {
            var (svc, _, engine) = NetflixPlaybackEngineTests.Engine();
            var player = new VideoPlayer(new FakeMpv()) { NetflixEngine = engine };
            var menu = 0;
            player.HistoryRequested += (_, _) => menu++;
            player.Source = WatchUrl;
            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));

            Press(player, Key.Right);
            Press(player, Key.Space);
            Press(player, Key.Down);
            await engine.WhenCommandsDoneAsync();

            Assert.Equal(new[] { (NetflixPlayerAction.SeekBy, 10.0), (NetflixPlayerAction.Pause, 0.0) }, svc.PlayerActions);
            Assert.Equal(1, menu);
        }

        [AvaloniaFact]
        public async Task DisneyUrl_UsesTheDisneyEngine_AndShowsTheBrowserOnceLoaded()
        {
            var (_, _, disney) = DisneyPlaybackEngineTests.Engine();
            var mpv = new FakeMpv();
            var player = new VideoPlayer(mpv);
            player.AddBrowserEngine(disney);

            player.Source = DisneyPlaybackEngineTests.SeriesUrl;

            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));
            Assert.Empty(mpv.Played);
            Assert.Equal(PlaybackState.Playing, disney.State);
        }

        [AvaloniaFact]
        public async Task DisneyLoading_ShowsTheDisneySteps()
        {
            var (_, _, disney) = DisneyPlaybackEngineTests.Engine();
            var player = new VideoPlayer(new FakeMpv());
            player.AddBrowserEngine(disney);
            var seen = new List<string>();
            player.PropertyChanged += (_, e) => { if (e.Property == VideoPlayer.LoadingTextProperty) seen.Add(player.LoadingText); };

            player.Source = DisneyPlaybackEngineTests.SeriesUrl;

            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));
            Assert.Contains("Starting Disney+…", seen);
            Assert.Contains("Loading the video…", seen);
        }

        [AvaloniaFact]
        public async Task DisneyFailure_ShowsTheReasonInTheLoadingOverlay()
        {
            var (_, _, disney) = DisneyPlaybackEngineTests.Engine(playsVideo: false);
            var player = new VideoPlayer(new FakeMpv());
            player.AddBrowserEngine(disney);

            player.Source = DisneyPlaybackEngineTests.SeriesUrl;

            Assert.True(await WaitUntil(() => player.LoadingText.EndsWith("Press Back to return.")));
            Assert.False(player.IsShowingBrowser);
            Assert.Contains("No video playback observed", player.LoadingText);
        }

        [AvaloniaFact]
        public async Task EachBrowserEngine_PlaysOnlyItsOwnUrls_AndMpvTheRest()
        {
            var (_, _, netflix) = NetflixPlaybackEngineTests.Engine();
            var (_, _, disney) = DisneyPlaybackEngineTests.Engine();
            var mpv = new FakeMpv();
            var player = new VideoPlayer(mpv) { NetflixEngine = netflix };
            player.AddBrowserEngine(disney);

            player.Source = "http://tvh/stream/1";
            Assert.True(await WaitUntil(() => mpv.Played.Contains("http://tvh/stream/1")));
            Assert.Equal(PlaybackState.Idle, netflix.State);
            Assert.Equal(PlaybackState.Idle, disney.State);

            player.Source = WatchUrl;
            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));
            Assert.Equal(PlaybackState.Playing, netflix.State);
            Assert.Equal(PlaybackState.Idle, disney.State);
        }

        [AvaloniaFact]
        public async Task SwitchingFromDisneyToMpv_StopsDisney_AndTheWindowIsOpaqueAgain()
        {
            var (_, service, disney) = DisneyPlaybackEngineTests.Engine();
            var mpv = new FakeMpv();
            var player = new VideoPlayer(mpv);
            player.AddBrowserEngine(disney);
            player.Source = DisneyPlaybackEngineTests.SeriesUrl;
            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));

            player.Source = "http://tvh/stream/1";

            Assert.True(await WaitUntil(() => mpv.Played.Contains("http://tvh/stream/1") && !player.IsShowingBrowser));
            Assert.Equal(PlaybackState.Idle, disney.State);
            Assert.True(await WaitUntil(() => service.Hub.ActivePlayback == null)); // the browser goes back to its idle page
        }

        [AvaloniaFact]
        public async Task SwitchingFromDisneyToNetflix_WaitsForDisneyToGiveBackTheTab()
        {
            var (_, dService, disney) = DisneyPlaybackEngineTests.Engine();
            var (_, _, netflix) = NetflixPlaybackEngineTests.Engine();
            var player = new VideoPlayer(new FakeMpv()) { NetflixEngine = netflix };
            player.AddBrowserEngine(disney);
            player.Source = DisneyPlaybackEngineTests.SeriesUrl;
            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));

            player.Source = WatchUrl;

            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading && netflix.State == PlaybackState.Playing));
            Assert.Equal(PlaybackState.Idle, disney.State);
            Assert.NotEqual("Disney+", dService.Hub.ActivePlayback);
        }

        [AvaloniaFact]
        public async Task ADisneyPrompt_ShowsOnThePlayer_AndOKAnswersItAndItGoesWhenTheEngineStops()
        {
            var (b, _, disney) = DisneyPlaybackEngineTests.Engine();
            var player = new VideoPlayer(new FakeMpv());
            player.AddBrowserEngine(disney);
            player.Source = DisneyPlaybackEngineTests.SeriesUrl;
            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));
            Assert.Equal("", player.PromptText);

            b.PollReply = () => FakeDisneyBrowser.Poll(30, prompt: "SKIP INTRO");
            await disney.PollOnceAsync();
            Assert.True(await WaitUntil(() => player.PromptText == "Skip intro"));

            Press(player, Key.Enter);
            await disney.WhenCommandsDoneAsync();
            Assert.Equal(1, b.PromptClicks);
            Assert.True(await WaitUntil(() => player.PromptText == ""));
        }

        [AvaloniaFact]
        public async Task OK_WithNoPrompt_DoesNothing()
        {
            var (b, _, disney) = DisneyPlaybackEngineTests.Engine();
            var player = new VideoPlayer(new FakeMpv());
            player.AddBrowserEngine(disney);
            player.Source = DisneyPlaybackEngineTests.SeriesUrl;
            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));

            Press(player, Key.Enter);
            await disney.WhenCommandsDoneAsync();

            Assert.Equal(0, b.PromptClicks);
            Assert.False(player.ActivatePrompt());
        }

        [AvaloniaFact]
        public async Task ThePrompt_GoesWhenTheViewerSwitchesToAnotherSource()
        {
            var (b, _, disney) = DisneyPlaybackEngineTests.Engine();
            var player = new VideoPlayer(new FakeMpv());
            player.AddBrowserEngine(disney);
            player.Source = DisneyPlaybackEngineTests.SeriesUrl;
            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));
            b.PollReply = () => FakeDisneyBrowser.Poll(30, prompt: "NEXT EPISODE");
            await disney.PollOnceAsync();
            Assert.True(await WaitUntil(() => player.PromptText == "Next episode"));

            player.Source = "http://tvh/stream/1";

            Assert.True(await WaitUntil(() => player.PromptText == ""));
        }

        [AvaloniaFact]
        public async Task QuickSpaceSpace_OverNetflix_PausesThenResumes()
        {
            var (svc, _, engine) = NetflixPlaybackEngineTests.Engine();
            var player = new VideoPlayer(new FakeMpv()) { NetflixEngine = engine };
            player.Source = WatchUrl;
            Assert.True(await WaitUntil(() => player.IsShowingBrowser && !player.IsLoading));

            Press(player, Key.Space);
            Press(player, Key.Space);
            await engine.WhenCommandsDoneAsync();

            Assert.Equal(new[] { (NetflixPlayerAction.Pause, 0.0), (NetflixPlayerAction.Play, 0.0) }, svc.PlayerActions);
        }

        [AvaloniaFact]
        public async Task NetflixFailure_HidesTheSpinner_UntilTheNextPlay()
        {
            var (_, _, engine) = NetflixPlaybackEngineTests.Engine(video: () => null);
            var player = new VideoPlayer(new FakeMpv()) { NetflixEngine = engine };
            player.Source = WatchUrl;
            Assert.True(await WaitUntil(() => player.IsShowingFailure));

            player.Source = "http://tvh/stream/1";
            Dispatcher.UIThread.RunJobs();

            Assert.False(player.IsShowingFailure);
        }

        [AvaloniaFact]
        public async Task NetflixUrl_WithoutNetflix_SaysWhy_AndDoesNotUseMpv()
        {
            var mpv = new FakeMpv();
            var player = new VideoPlayer(mpv);

            player.Source = WatchUrl;

            Assert.True(await WaitUntil(() => player.IsShowingFailure));
            Assert.Contains("Netflix is not set up", player.LoadingText);
            Assert.Empty(mpv.Played);
        }
    }
}
