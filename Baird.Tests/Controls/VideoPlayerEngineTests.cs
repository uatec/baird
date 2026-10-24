using System;
using System.Collections.Generic;
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
    }
}
