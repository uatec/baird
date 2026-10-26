using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Baird.Services;
using Baird.ViewModels;
using Microsoft.Extensions.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace Baird.Tests.Services
{
    /// <summary>
    /// Opt-in (BAIRD_NETFLIX_SIGNIN_LIVE=1): signs in to the REAL Netflix with NETFLIX_USER/NETFLIX_PASS from ~/.baird/config.ini,
    /// through the spike's flow, in a throwaway profile. One attempt, no retries. Prints states/log, never the password.
    /// Use BAIRD_CHROME_EXTRA_ARGS="--headless=new --no-sandbox" without a display.
    /// </summary>
    public class NetflixLiveSignInTests
    {
        private readonly ITestOutputHelper _out;
        public NetflixLiveSignInTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public async Task ConfiguredCredentials_SignIn_ThroughSpikeFlow()
        {
            if (Environment.GetEnvironmentVariable("BAIRD_NETFLIX_SIGNIN_LIVE") != "1") return;

            var ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".baird", "config.ini");
            var cfg = new ConfigurationBuilder().AddIniFile(ini, optional: false).Build();
            var creds = NetflixCredentials.FromConfiguration(cfg);
            Assert.NotNull(creds);

            var vm = new NetflixPlaybackFlow(creds)
            {
                ProfileDir = Environment.GetEnvironmentVariable("BAIRD_NETFLIX_TEST_PROFILE") ?? Path.Combine(Path.GetTempPath(), "baird-nf-signin-" + Guid.NewGuid().ToString("N")),
                BufferingTimeout = TimeSpan.FromSeconds(15)
            };
            try
            {
                await vm.LaunchPlaybackFlowAsync();
            }
            finally
            {
                foreach (var l in vm.LogEntries) _out.WriteLine(l);
                await vm.StopPlaybackAsync(); // never leave a signed-in, playing browser behind
                try { Directory.Delete(vm.ProfileDir, true); } catch { } // the profile holds a live Netflix session
            }
            _out.WriteLine($"FINAL STATE: {vm.State} | {vm.StatusMessage}");

            Assert.DoesNotContain(creds!.Password, vm.StatusMessage);
            Assert.DoesNotContain(vm.LogEntries, l => l.Contains(creds.Password));
            Assert.True(vm.State != NetflixPlaybackFlowState.NotLoggedIn && vm.State != NetflixPlaybackFlowState.Failed, vm.StatusMessage);
        }
    }
}
