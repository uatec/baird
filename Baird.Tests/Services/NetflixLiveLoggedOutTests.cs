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
    /// Opt-in (BAIRD_NETFLIX_LIVE=1): loads the real netflix.com/watch page in a throwaway, logged-out profile and checks
    /// the spike reports "not signed in". Anonymous page load only: no credentials, never signs in.
    /// Use BAIRD_CHROME_EXTRA_ARGS="--headless=new --no-sandbox" without a display.
    /// </summary>
    public class NetflixLiveLoggedOutTests
    {
        private readonly ITestOutputHelper _out;
        public NetflixLiveLoggedOutTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public async Task LoggedOutProfile_IsReportedAsNotSignedIn()
        {
            if (Environment.GetEnvironmentVariable("BAIRD_NETFLIX_LIVE") != "1") return;

            var profile = Path.Combine(Path.GetTempPath(), "baird-nf-live-" + Guid.NewGuid().ToString("N"));
            await using var svc = new CdpBrowserService();
            svc.LogMessage += m => _out.WriteLine(m);
            var ct = new CancellationTokenSource(TimeSpan.FromSeconds(90)).Token;

            Assert.True(await svc.LaunchAsync(svc.FindBrowserExecutable(), "https://www.netflix.com/watch/80107105", 9334, profile, false, ct), svc.LastError);
            AuthPageStatus? st = null;
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000, ct);
                st = await svc.CheckAuthStatusAsync(ct);
                if (st != null && st.Url.Contains("netflix.com") && st.MembershipStatus != null) break;
            }
            _out.WriteLine($"LIVE: {st}");
            Assert.NotNull(st);
            Assert.Equal("ANONYMOUS", st!.MembershipStatus);
            Assert.True(st.HasSignInLink);
            Assert.Equal(NetflixPageKind.LoginRequired, st.Kind);
        }
    }
}
