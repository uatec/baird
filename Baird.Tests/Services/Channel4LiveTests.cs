using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Services;
using Xunit;
using Xunit.Abstractions;

namespace Baird.Tests.Services
{
    /// <summary>
    /// Opt-in (BAIRD_CHANNEL4_LIVE=1): checks the shapes Baird relies on against the real channel4.com (UK only). Anonymous: no
    /// credentials, never signs in. The browser test needs Chrome; use BAIRD_CHROME_EXTRA_ARGS="--headless=new" without a display.
    /// </summary>
    public class Channel4LiveTests
    {
        private readonly ITestOutputHelper _out;
        public Channel4LiveTests(ITestOutputHelper o) => _out = o;

        private static bool Enabled => Environment.GetEnvironmentVariable("BAIRD_CHANNEL4_LIVE") == "1";

        [Fact]
        public async Task Search_and_a_programme_page_read_as_expected()
        {
            if (!Enabled) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var catalogue = new Channel4Catalogue();

            var results = await catalogue.SearchAsync("derry girls", cts.Token);
            _out.WriteLine(string.Join(" | ", results.Select(r => $"{r.Title} ({r.Slug}, available {r.Available}, film {r.LooksLikeFilm})")));
            var dg = Assert.Single(results, r => r.Slug == "derry-girls");
            Assert.True(dg.Available);

            var programme = await catalogue.GetProgrammeAsync("derry-girls", cts.Token);
            _out.WriteLine($"{programme.Title}: {programme.ProgrammeType}, series {string.Join(",", programme.SeriesNumbers)}, {programme.Episodes.Count} episodes");
            Assert.False(programme.IsFilm);
            Assert.True(programme.Episodes.Count >= 10);
            Assert.All(programme.Episodes, e => Assert.True(e.RuntimeSeconds > 0, e.ProgrammeId));
        }

        [Fact]
        public async Task A_signed_out_browser_on_an_on_demand_page_is_seen_as_needing_a_sign_in()
        {
            if (!Enabled) return;
            var profile = Path.Combine(Path.GetTempPath(), "baird-c4-live-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using var browser = new CdpBrowserService();
                browser.LogMessage += m => _out.WriteLine(m);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                const string url = "https://www.channel4.com/programmes/derry-girls/on-demand/59741-001";
                Assert.True(await browser.LaunchAsync(browser.FindBrowserExecutable(), url, 9336, profile, false, cts.Token), browser.LastError);

                Channel4PageState? state = null;
                for (int i = 0; i < 20 && state is not { Authenticated: not null }; i++)
                {
                    await Task.Delay(1000, cts.Token);
                    state = await Channel4PlayerControl.ReadStateAsync(browser, cts.Token);
                }
                _out.WriteLine($"LIVE: {state}");
                Assert.NotNull(state);
                Assert.False(state!.Authenticated);
                Assert.True(state.SignInPrompt);
                Assert.True(state.NeedsSignIn);

                Assert.True(await browser.NavigateAsync(Channel4Service.SignInUrl + "?redirectUrl=" + Uri.EscapeDataString(url), cts.Token));
                for (int i = 0; i < 20 && state is not { EmailField: true }; i++)
                {
                    await Task.Delay(1000, cts.Token);
                    state = await Channel4PlayerControl.ReadStateAsync(browser, cts.Token);
                }
                _out.WriteLine($"LIVE sign-in page: {state}");
                Assert.True(state!.EmailField);
            }
            finally
            {
                try { Directory.Delete(profile, true); } catch { }
            }
        }
    }
}
