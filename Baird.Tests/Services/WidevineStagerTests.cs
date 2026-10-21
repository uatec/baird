using System;
using System.IO;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    public class WidevineStagerTests : IDisposable
    {
        private readonly string _tmp = Path.Combine(Path.GetTempPath(), "wvstage-" + Guid.NewGuid().ToString("N"));

        public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

        private string MakeSystemWidevine(string version = "4.10.2662.3")
        {
            var sys = Path.Combine(_tmp, "opt");
            Directory.CreateDirectory(Path.Combine(sys, "gmp-widevinecdm", "latest"));
            File.WriteAllText(Path.Combine(sys, "manifest.json"), "{\"version\":\"" + version + "\"}");
            File.WriteAllBytes(Path.Combine(sys, "gmp-widevinecdm", "latest", "libwidevinecdm.so"), new byte[] { 1, 2, 3 });
            return sys;
        }

        [Fact]
        public void Stage_CreatesVersionedLayoutAndHint()
        {
            var sys = MakeSystemWidevine();
            var profile = Path.Combine(_tmp, "profile");
            var r = WidevineStager.Stage(profile, sys, "linux_arm64");
            Assert.True(r.Staged, r.Detail);
            var ver = Path.Combine(profile, "WidevineCdm", "4.10.2662.3");
            Assert.True(File.Exists(Path.Combine(ver, "_platform_specific", "linux_arm64", "libwidevinecdm.so")));
            Assert.True(File.Exists(Path.Combine(ver, "manifest.json")));
            Assert.Contains("4.10.2662.3", File.ReadAllText(Path.Combine(profile, "WidevineCdm", "latest-component-updated-widevine-cdm")));
        }

        [Fact]
        public void Stage_IsIdempotent()
        {
            var sys = MakeSystemWidevine();
            var profile = Path.Combine(_tmp, "profile");
            Assert.True(WidevineStager.Stage(profile, sys, "linux_x64").Staged);
            Assert.True(WidevineStager.Stage(profile, sys, "linux_x64").Staged);
        }

        [Fact]
        public void Stage_ReportsMissingSystemWidevine()
        {
            var r = WidevineStager.Stage(Path.Combine(_tmp, "profile"), Path.Combine(_tmp, "nope"), "linux_x64");
            Assert.False(r.Staged);
            Assert.Contains("no system Widevine", r.Detail);
        }
    }
}
