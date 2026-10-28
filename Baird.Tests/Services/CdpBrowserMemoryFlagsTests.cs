using System.Linq;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    public class CdpBrowserMemoryFlagsTests
    {
        private static string[] Flags => CdpBrowserService.MemoryFlags.Split(' ');

        [Fact]
        public void Chromium_is_limited_in_renderer_processes_and_javascript_heap()
        {
            Assert.Contains("--renderer-process-limit=2", Flags);
            Assert.Contains("--js-flags=--max-old-space-size=512", Flags);
        }

        [Fact]
        public void Every_disabled_feature_is_in_one_flag_because_Chromium_honours_only_the_last()
        {
            Assert.Single(Flags.Where(f => f.StartsWith("--disable-features=")));
            Assert.Contains("site-per-process", Flags.Single(f => f.StartsWith("--disable-features=")));
        }

        [Fact]
        public void Nothing_that_Widevine_or_sign_in_needs_is_switched_off()
        {
            // Widevine is staged from /opt, but the component updater and extensions are left alone, and cookies/profile are untouched.
            Assert.DoesNotContain(Flags, f => f.StartsWith("--disable-component-update") || f.StartsWith("--disable-extensions") || f.StartsWith("--incognito"));
        }
    }
}
