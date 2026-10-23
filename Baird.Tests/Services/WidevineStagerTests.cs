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

            // Second launch: the same version is already there, so nothing is rewritten.
            var again = WidevineStager.Stage(profile, sys, "linux_x64");
            Assert.False(again.Staged);
            Assert.Contains("leaving it alone", again.Detail);
            Assert.True(File.Exists(Path.Combine(profile, "WidevineCdm", "4.10.2662.3", "_platform_specific", "linux_x64", "libwidevinecdm.so")));
        }

        [Fact]
        public void Stage_ReportsMissingSystemWidevine()
        {
            var r = WidevineStager.Stage(Path.Combine(_tmp, "profile"), Path.Combine(_tmp, "nope"), "linux_x64");
            Assert.False(r.Staged);
            Assert.Contains("no system Widevine", r.Detail);
        }

        private string MakeProfileWidevine(string profile, string version, string platform = "linux_arm64")
        {
            var dir = Path.Combine(profile, "WidevineCdm", version);
            Directory.CreateDirectory(Path.Combine(dir, "_platform_specific", platform));
            File.WriteAllText(Path.Combine(dir, "manifest.json"), "{\"version\":\"" + version + "\"}");
            File.WriteAllBytes(Path.Combine(dir, "_platform_specific", platform, "libwidevinecdm.so"), new byte[] { 9 });
            var hint = Path.Combine(profile, "WidevineCdm", "latest-component-updated-widevine-cdm");
            File.WriteAllText(hint, "{\"Path\":\"" + dir + "\"}");
            return hint;
        }

        [Fact]
        public void Stage_NeverDowngradesAComponentUpdatedWidevine()
        {
            // The Pi's case: Chromium downloaded 4.10.3112.0 itself; the system copy is the 2023 4.10.2662.3.
            var sys = MakeSystemWidevine("4.10.2662.3");
            var profile = Path.Combine(_tmp, "profile");
            var hint = MakeProfileWidevine(profile, "4.10.3112.0");
            var before = File.ReadAllText(hint);

            var r = WidevineStager.Stage(profile, sys, "linux_arm64");

            Assert.False(r.Staged);
            Assert.Contains("4.10.3112.0", r.Detail);
            Assert.Equal(before, File.ReadAllText(hint)); // hint still points at the newer CDM
            Assert.False(Directory.Exists(Path.Combine(profile, "WidevineCdm", "4.10.2662.3")));
        }

        [Fact]
        public void Stage_UpgradesWhenTheProfileOnlyHasAnOlderWidevine()
        {
            var sys = MakeSystemWidevine("4.10.3112.0");
            var profile = Path.Combine(_tmp, "profile");
            MakeProfileWidevine(profile, "4.10.2662.3");

            var r = WidevineStager.Stage(profile, sys, "linux_arm64");

            Assert.True(r.Staged, r.Detail);
            Assert.Contains("4.10.3112.0", File.ReadAllText(Path.Combine(profile, "WidevineCdm", "latest-component-updated-widevine-cdm")));
        }

        [Fact]
        public void Stage_RepairsAHintThatPointsAtAnOlderCopy()
        {
            // State left by earlier Baird versions: Chromium downloaded 3112, Baird forced the hint back to 2662.3.
            var sys = MakeSystemWidevine("4.10.2662.3");
            var profile = Path.Combine(_tmp, "profile");
            MakeProfileWidevine(profile, "4.10.3112.0");
            var oldHint = MakeProfileWidevine(profile, "4.10.2662.3"); // hint now points at the old one

            var r = WidevineStager.Stage(profile, sys, "linux_arm64");

            Assert.False(r.Staged);
            Assert.Contains("re-pointed", r.Detail);
            Assert.Contains("4.10.3112.0", File.ReadAllText(oldHint));
        }
    }
}
