using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baird.Services
{
    /// <summary>
    /// Debian/Pi OS installs Widevine to /opt/WidevineCdm (libwidevinecdm0), but Chromium there reports
    /// "Widevine enabled but no library found" and denies com.widevine.alpha. Chromium does accept the same files
    /// when they sit in the profile as WidevineCdm/&lt;version&gt;/ with a latest-component-updated-widevine-cdm hint,
    /// so we stage them there before launching, but only when the profile has no Widevine of its own (or an older one).
    /// </summary>
    public static class WidevineStager
    {
        public const string DefaultSystemDir = "/opt/WidevineCdm";

        public record Result(bool Staged, string Detail);

        /// <summary>Highest Widevine version present in the profile's WidevineCdm folder with a library for this platform.</summary>
        private static (string Version, string Dir)? NewestInstalled(string root, string platformDir)
        {
            if (!Directory.Exists(root)) return null;
            (string Version, string Dir)? best = null;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var manifest = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifest) || !File.Exists(Path.Combine(dir, "_platform_specific", platformDir, "libwidevinecdm.so"))) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                    var v = doc.RootElement.TryGetProperty("version", out var vp) ? vp.GetString() : null;
                    if (string.IsNullOrEmpty(v)) continue;
                    if (best == null || ParseVersion(v) > ParseVersion(best.Value.Version)) best = (v, dir);
                }
                catch { /* unreadable manifest: ignore that folder */ }
            }
            return best;
        }

        private static string? HintTarget(string hintPath)
        {
            try
            {
                if (!File.Exists(hintPath)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(hintPath));
                return doc.RootElement.TryGetProperty("Path", out var p) ? p.GetString()?.TrimEnd('/') : null;
            }
            catch { return null; }
        }

        private static Version ParseVersion(string v) => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0);

        public static Result Stage(string userDataDir, string systemDir = DefaultSystemDir, string? platformDir = null)
        {
            try
            {
                var manifestPath = Path.Combine(systemDir, "manifest.json");
                if (!File.Exists(manifestPath)) return new Result(false, $"no system Widevine at {systemDir}");

                string version;
                using (var doc = JsonDocument.Parse(File.ReadAllText(manifestPath)))
                {
                    version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                }
                if (string.IsNullOrWhiteSpace(version)) return new Result(false, "system Widevine manifest has no version");

                platformDir ??= RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux_arm64" : "linux_x64";
                var lib = Path.Combine(systemDir, "_platform_specific", platformDir, "libwidevinecdm.so");
                if (!File.Exists(lib)) lib = Path.Combine(systemDir, "gmp-widevinecdm", "latest", "libwidevinecdm.so");
                if (!File.Exists(lib)) return new Result(false, $"libwidevinecdm.so not found under {systemDir}");

                var root = Path.Combine(userDataDir, "WidevineCdm");

                // Never downgrade. Chromium's component updater downloads a newer Widevine into the profile by itself (measured on
                // the Pi: 4.10.3112.0 vs the 2023 system 4.10.2662.3), and Netflix refuses the old one with error E100. Re-pointing
                // the hint at the system copy on every launch was what made Netflix fail. Only bootstrap a profile that has no
                // Widevine, or only an older one.
                var newest = NewestInstalled(root, platformDir);
                if (newest != null && ParseVersion(newest.Value.Version) >= ParseVersion(version))
                {
                    // Earlier Baird versions forced the hint back to the old system copy; repair that so Chromium loads the newest.
                    var hintPath = Path.Combine(root, "latest-component-updated-widevine-cdm");
                    if (HintTarget(hintPath) != newest.Value.Dir)
                    {
                        File.WriteAllText(hintPath, new JsonObject { ["Path"] = newest.Value.Dir }.ToJsonString());
                        return new Result(false, $"profile already has Widevine {newest.Value.Version}; re-pointed Chromium's hint at it (it pointed at an older copy)");
                    }
                    return new Result(false, $"profile already has Widevine {newest.Value.Version} (system copy is {version}); leaving it alone");
                }

                var versionDir = Path.Combine(root, version);
                var destLib = Path.Combine(versionDir, "_platform_specific", platformDir, "libwidevinecdm.so");
                if (!File.Exists(destLib) || new FileInfo(destLib).Length != new FileInfo(lib).Length)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destLib)!);
                    File.Copy(lib, destLib, overwrite: true);
                    File.Copy(manifestPath, Path.Combine(versionDir, "manifest.json"), overwrite: true);
                }
                File.WriteAllText(Path.Combine(root, "latest-component-updated-widevine-cdm"),
                    new JsonObject { ["Path"] = versionDir }.ToJsonString());
                return new Result(true, $"staged Widevine {version} into {versionDir}");
            }
            catch (Exception e)
            {
                return new Result(false, "staging failed: " + e.Message);
            }
        }
    }
}
