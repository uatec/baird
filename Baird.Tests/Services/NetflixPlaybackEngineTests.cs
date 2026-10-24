using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Baird.Mpv;
using Baird.Services;
using Baird.Tests.ViewModels;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.Services
{
    public class NetflixPlaybackEngineTests
    {
        private static readonly string WatchUrl = NetflixProvider.WatchUrl("80107103");

        private static VideoPlaybackStatus Video(double t, bool paused = false, double duration = 1500) =>
            new(true, t, 4, paused, duration, WatchUrl, "Netflix");

        private static VideoPlaybackStatus NoVideo() => new(false, 0, 0, false, 0, WatchUrl, "Netflix");

        private static (MockCdpBrowserService svc, NetflixSession session, NetflixPlaybackEngine engine) Engine(Func<VideoPlaybackStatus?>? video = null)
        {
            double t = 0;
            var svc = new MockCdpBrowserService
            {
                AuthStatusToReturn = new AuthPageStatus(WatchUrl, "t", false, false, false, 0),
                VideoSequence = video ?? (() => Video(t += 0.4)),
                SessionSequence = () => new NetflixSessionState("https://www.netflix.com/browse", false, true, Array.Empty<string>()),
            };
            var session = new NetflixSession(svc, null) { PollInterval = TimeSpan.FromMilliseconds(5), SettleDelay = TimeSpan.FromMilliseconds(5) };
            var engine = new NetflixPlaybackEngine(session, () => new NetflixSpikeViewModel(svc)
            {
                PollInterval = TimeSpan.FromMilliseconds(5),
                AuthCheckTimeout = TimeSpan.FromMilliseconds(200),
                BufferingTimeout = TimeSpan.FromMilliseconds(300),
            })
            {
                PollInterval = TimeSpan.FromHours(1), // tests poll by hand
            };
            return (svc, session, engine);
        }

        [Theory]
        [InlineData("https://www.netflix.com/watch/80107103", true)]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData("https://www.netflix.com/browse", false)]
        [InlineData("http://tvh/stream/1", false)]
        public void Handles_OnlyNetflixWatchUrls(string? url, bool expected)
        {
            Assert.Equal(expected, NetflixPlaybackEngine.Handles(url));
        }

        [Fact]
        public async Task Play_ReachesPlaying_RaisesFileLoaded_AndMarksSessionBusy()
        {
            var (svc, session, engine) = Engine();
            var loaded = 0;
            engine.FileLoaded += (_, _) => loaded++;

            await engine.PlayAsync(WatchUrl);

            Assert.Equal(1, loaded);
            Assert.Equal(PlaybackState.Playing, engine.State);
            Assert.True(session.IsPlaybackActive);
            Assert.False(svc.RevealWindowCalled);
            Assert.Contains(WatchUrl, svc.NavigatedUrls);
        }

        [Fact]
        public async Task Loading_ReportsStepText()
        {
            var (_, _, engine) = Engine();
            var texts = new List<string>();
            engine.StatusChanged += (_, _) => texts.Add(engine.StatusText);

            await engine.PlayAsync(WatchUrl);

            Assert.Contains("Starting Netflix…", texts);
            Assert.Contains("Loading the video…", texts);
        }

        [Fact]
        public async Task Poll_ReportsPositionDurationAndPause()
        {
            double t = 0;
            VideoPlaybackStatus? current = null; // advancing until the test sets a value
            var (svc, _, engine) = Engine(() => current ?? Video(t += 0.4));
            double? duration = null;
            bool? paused = null;
            engine.DurationChanged += (_, d) => duration = d;
            engine.PauseStateChanged += (_, p) => paused = p;
            await engine.PlayAsync(WatchUrl);

            current = Video(42, paused: true, duration: 1500);
            await engine.PollOnceAsync();

            Assert.Equal(42, engine.TimePositionSeconds);
            Assert.Equal(1500, engine.DurationSeconds);
            Assert.Equal(1500, duration);
            Assert.True(paused);
            Assert.Equal(PlaybackState.Paused, engine.State);
        }

        [Fact]
        public async Task SeekRelative_And_Pause_UseThePlayerApi()
        {
            var (svc, _, engine) = Engine();
            await engine.PlayAsync(WatchUrl);

            engine.SeekRelative(10);
            engine.Pause();
            await engine.WhenCommandsDoneAsync();

            Assert.Equal(new[] { (NetflixPlayerAction.SeekBy, 10.0), (NetflixPlayerAction.Pause, 0.0) }, svc.PlayerActions);
        }

        [Fact]
        public async Task Stop_ReturnsBrowserToIdle_AndFreesSession()
        {
            var (svc, session, engine) = Engine();
            await engine.PlayAsync(WatchUrl);

            await engine.StopAsync();

            Assert.Equal(NetflixSession.IdleUrl, svc.LastNavigatedUrl);
            Assert.False(session.IsPlaybackActive);
            Assert.Equal(PlaybackState.Idle, engine.State);
        }

        [Fact]
        public async Task VideoGone_ThreePolls_RaisesStreamEndedOnce()
        {
            double t = 0;
            VideoPlaybackStatus? current = null;
            var (svc, session, engine) = Engine(() => current ?? Video(t += 0.4));
            var ended = 0;
            engine.StreamEnded += (_, _) => ended++;
            await engine.PlayAsync(WatchUrl);

            current = NoVideo();
            for (var i = 0; i < 5; i++) await engine.PollOnceAsync();

            Assert.Equal(1, ended);
            Assert.Equal(PlaybackState.Idle, engine.State);
            Assert.False(session.IsPlaybackActive);
            Assert.Equal(NetflixSession.IdleUrl, svc.LastNavigatedUrl);
        }

        [Fact]
        public async Task FlowFails_RaisesFailedWithReason_AndFreesTheBrowser()
        {
            var (_, session, engine) = Engine(video: () => null);
            var reasons = new List<string>();
            engine.Failed += (_, r) => reasons.Add(r);

            await engine.PlayAsync(WatchUrl);

            Assert.Single(reasons);
            Assert.False(string.IsNullOrWhiteSpace(reasons[0]));
            Assert.False(session.IsPlaybackActive);
            Assert.Equal(PlaybackState.Idle, engine.State);
        }

        [Fact]
        public async Task PollNull_RaisesFailedOnce()
        {
            double t = 0;
            var gone = false;
            var (_, _, engine) = Engine(() => gone ? null : Video(t += 0.4));
            var failed = 0;
            engine.Failed += (_, _) => failed++;
            await engine.PlayAsync(WatchUrl);

            gone = true;
            await engine.PollOnceAsync();
            await engine.PollOnceAsync();

            Assert.Equal(1, failed);
        }
    }
}
