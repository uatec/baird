using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Baird.Mpv
{
    /// <summary>
    /// Baird's yt-dlp is a PyInstaller one-file binary: every run unpacks ~85 MB into $TMPDIR/_MEIxxxxxx and only removes it on
    /// a clean exit. mpv kills it when a load is replaced or times out, so the folders leaked into /tmp, which on the Pi is a
    /// RAM-backed 4 GB tmpfs (measured: 60 folders, 4.0 GB, 100% full). Give yt-dlp its own disk-backed temp folder and clear
    /// stale leftovers there at startup. Only Baird's yt-dlp writes there, so nothing else's files are touched.
    /// </summary>
    public static class YtDlpTempDir
    {
        [DllImport("libc", SetLastError = true)]
        private static extern int setenv(string name, string value, int overwrite);

        public static string DefaultPath => Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
            "baird", "ytdlp-tmp");

        /// <summary>
        /// Call once at startup, before any threads or libmpv: setenv is not thread-safe. Sets the native TMPDIR, which
        /// libmpv's yt-dlp child inherits (.NET's Environment.SetEnvironmentVariable would not reach native code).
        /// </summary>
        public static void Configure()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
            try
            {
                var dir = DefaultPath;
                Directory.CreateDirectory(dir);
                var removed = CleanStale(dir, TimeSpan.FromMinutes(30));
                setenv("TMPDIR", dir, 1);
                Console.WriteLine($"[YtDlpTempDir] yt-dlp temp folder: {dir} (removed {removed} stale _MEI folder(s))");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[YtDlpTempDir] Could not set up yt-dlp temp folder: {ex.Message}");
            }
        }

        /// <summary>Deletes _MEI* folders in <paramref name="dir"/> not modified for <paramref name="olderThan"/>. Returns how many.</summary>
        public static int CleanStale(string dir, TimeSpan olderThan, DateTime? nowUtc = null)
        {
            if (!Directory.Exists(dir)) return 0;
            var cutoff = (nowUtc ?? DateTime.UtcNow) - olderThan;
            int removed = 0;
            foreach (var sub in Directory.EnumerateDirectories(dir, "_MEI*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(sub) < cutoff)
                    {
                        Directory.Delete(sub, recursive: true);
                        removed++;
                    }
                }
                catch { /* in use or already gone */ }
            }
            return removed;
        }
    }
}
