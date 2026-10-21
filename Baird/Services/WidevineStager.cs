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
    /// so we stage them there before launching.
    /// </summary>
    public static class WidevineStager
    {
        public const string DefaultSystemDir = "/opt/WidevineCdm";

        public record Result(bool Staged, string Detail);

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
