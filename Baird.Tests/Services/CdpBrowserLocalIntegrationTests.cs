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
    }
}
