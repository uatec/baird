using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Baird.Services;
using Xunit;
using Xunit.Abstractions;

namespace Baird.Tests.Services
{
    /// <summary>
    /// Opt-in: set BAIRD_CDP_IT_URL_BASE (e.g. http://localhost:8765) to a server hosting video.html and login/index.html,
    /// and BAIRD_CHROME_EXTRA_ARGS=--headless=new if there is no display. Never touches Netflix.
    /// </summary>
    public class CdpBrowserLocalIntegrationTests
    {
        private readonly ITestOutputHelper _out;
        public CdpBrowserLocalIntegrationTests(ITestOutputHelper o) => _out = o;

        private static string? Base => Environment.GetEnvironmentVariable("BAIRD_CDP_IT_URL_BASE");

        [Fact]
        public async Task LocalPages_LoginDetected_VideoAdvances_WidevineReported()
        {
            if (string.IsNullOrEmpty(Base)) return; // opt-in only

            var profile = Path.Combine(Path.GetTempPath(), "baird-it-" + Guid.NewGuid().ToString("N"));
            await using var svc = new CdpBrowserService();
            svc.LogMessage += m => _out.WriteLine(m);
            var ct = new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

            Assert.True(await svc.LaunchAsync(svc.FindBrowserExecutable(), Base + "/login/index.html", 9333, profile, false, ct), svc.LastError);
            await Task.Delay(1000, ct);
            var login = await svc.CheckAuthStatusAsync(ct);
            _out.WriteLine($"login page: {login}");
            Assert.NotNull(login);
            Assert.Equal(NetflixPageKind.LoginRequired, login!.Kind);

            var wv = await svc.CheckWidevineAsync(ct);
            _out.WriteLine($"WIDEVINE available={wv.Available} detail={wv.Detail}");

            Assert.True(await svc.LaunchAsync(svc.FindBrowserExecutable(), Base + "/video.html", 9333, profile, false, ct), svc.LastError);
            await Task.Delay(1500, ct);
            var a = await svc.PollVideoPlaybackAsync(ct);
            await Task.Delay(1000, ct);
            var b = await svc.PollVideoPlaybackAsync(ct);
            _out.WriteLine($"video t1={a?.CurrentTime} t2={b?.CurrentTime}");
            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.True(b!.CurrentTime > a!.CurrentTime);
            var page = await svc.CheckAuthStatusAsync(ct);
            Assert.Equal(NetflixPageKind.VideoPlaying, page!.Kind);
        }

        /// <summary>Opt-in: set BAIRD_CDP_IT_BROWSER=1 (and BAIRD_CHROME_EXTRA_ARGS=--headless=new if there is no display).</summary>
        [Fact]
        public async Task OverlayBackKey_SignalsBairdToExit()
        {
            if (Environment.GetEnvironmentVariable("BAIRD_CDP_IT_BROWSER") != "1") return; // opt-in only

            var profile = Path.Combine(Path.GetTempPath(), "baird-it-" + Guid.NewGuid().ToString("N"));
            await using var svc = new CdpBrowserService();
            svc.LogMessage += m => _out.WriteLine(m);
            var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            svc.PageSignalReceived += p => signal.TrySetResult(p);
            var ct = new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;

            Assert.True(await svc.LaunchAsync(svc.FindBrowserExecutable(), "about:blank", 9334, profile, false, ct), svc.LastError);
            Assert.True(await svc.InjectOverlayScriptAsync(null, ct));
            await svc.InjectOverlayScriptAsync("window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' })); true", ct);

            var done = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(5), ct));
            Assert.True(done == signal.Task, "no page signal within 5 s");
            Assert.Equal("{\"type\":\"exit\"}", await signal.Task);
        }

        /// <summary>Opt-in: BAIRD_CDP_IT_BROWSER=1. Uses a fake Netflix player API, so it never touches Netflix.</summary>
        [Fact]
        public async Task NetflixPlayerCommands_UseThePlayerApi_ElseTheVideoElement()
        {
            if (Environment.GetEnvironmentVariable("BAIRD_CDP_IT_BROWSER") != "1") return; // opt-in only

            var profile = Path.Combine(Path.GetTempPath(), "baird-it-" + Guid.NewGuid().ToString("N"));
            await using var svc = new CdpBrowserService();
            svc.LogMessage += m => _out.WriteLine(m);
            var ct = new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token;
            Assert.True(await svc.LaunchAsync(svc.FindBrowserExecutable(), "about:blank", 9335, profile, false, ct), svc.LastError);

            Assert.Null(await svc.ControlNetflixPlayerAsync(NetflixPlayerAction.Play, 0, ct));

            await svc.InjectOverlayScriptAsync(@"
                window.calls = [];
                const player = { t: 60000,
                    getCurrentTime() { return this.t; }, getDuration() { return 100000; },
                    seek(ms) { window.calls.push('seek:' + ms); this.t = ms; },
                    play() { window.calls.push('play'); }, pause() { window.calls.push('pause'); } };
                window.netflix = { appContext: { state: { playerApp: { getAPI: () => ({ videoPlayer: {
                    getAllPlayerSessionIds: () => ['motion-billboard-1', 'watch-2'],
                    getVideoPlayerBySessionId: id => id === 'watch-2' ? player : null } }) } } } };
                true", ct);

            Assert.Equal("api", await svc.ControlNetflixPlayerAsync(NetflixPlayerAction.SeekBy, 10, ct));
            Assert.Equal("api", await svc.ControlNetflixPlayerAsync(NetflixPlayerAction.SeekBy, -100, ct));
            Assert.Equal("api", await svc.ControlNetflixPlayerAsync(NetflixPlayerAction.SeekBy, 500, ct));
            Assert.Equal("api", await svc.ControlNetflixPlayerAsync(NetflixPlayerAction.Pause, 0, ct));
            Assert.Equal("api", await svc.ControlNetflixPlayerAsync(NetflixPlayerAction.Play, 0, ct));
            await svc.InjectOverlayScriptAsync("document.title = window.calls.join(','); true", ct);
            var status = await svc.PollVideoPlaybackAsync(ct);
            Assert.Equal("seek:70000,seek:0,seek:99000,pause,play", status!.Title);
        }
    }
}
