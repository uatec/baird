using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>
    /// The one Chromium that Baird shares between streaming services (Netflix, Disney+). They share one browser profile, and
    /// Chromium locks a profile to one process, so there is one browser, one window and one tab. Each service keeps its
    /// own session state; the hub only arbitrates:
    /// <list type="bullet">
    /// <item><see cref="Gate"/>: one service uses the tab at a time (search, sign-in).</item>
    /// <item><see cref="PageOwner"/>: which service the tab was last navigated for. A service that finds it does not own the
    /// page navigates to its own idle page first (e.g. Netflix's search is a same-origin fetch, so it must be on netflix.com).</item>
    /// <item><see cref="ActivePlayback"/>: the service that is playing. The others must not touch the tab until it ends.</item>
    /// </list>
    /// </summary>
    public sealed class BrowserHub
    {
        public const int DefaultPort = 9222;

        public BrowserHub(ICdpBrowserService browser) => Browser = browser;

        public ICdpBrowserService Browser { get; }

        /// <summary>Held while a service uses the tab for a short job. Playback does not hold it; it sets <see cref="ActivePlayback"/>.</summary>
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Port { get; set; } = DefaultPort;

        public string ProfileDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "baird", "browser-profile");

        /// <summary>The service the page was last navigated for, or null when nobody has used it yet.</summary>
        public string? PageOwner { get; set; }

        /// <summary>The service that is playing now, or null.</summary>
        public string? ActivePlayback { get; set; }

        /// <summary>Why <paramref name="service"/> may not use the browser now (another service is playing), else null.</summary>
        public string? BlockedFor(string service) =>
            ActivePlayback is { } p && p != service ? $"{p} is playing; wait until playback ends." : null;

        /// <summary>
        /// Waits until no OTHER service is playing. VideoPlayer stops the previous engine when the viewer picks something else,
        /// but that stop finishes asynchronously (the tab goes back to its idle page, then the flag is cleared), so the next
        /// service must wait for it before it takes the tab. Returns false when the other service is still playing after
        /// <paramref name="timeout"/>.
        /// </summary>
        public async Task<bool> WaitForOtherPlaybackToEndAsync(string service, TimeSpan timeout, CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (BlockedFor(service) != null)
            {
                if (sw.Elapsed >= timeout) return false;
                await Task.Delay(50, ct);
            }
            return true;
        }

        /// <summary>
        /// Starts the browser on <paramref name="startUrl"/> unless it is already open (opened by any service). Returns null on
        /// success (and says whether it was already open), else the reason.
        /// </summary>
        public async Task<string?> EnsureRunningAsync(string startUrl, CancellationToken ct)
        {
            if (Browser.IsRunning) return null;
            if (!await Browser.LaunchAsync(Browser.FindBrowserExecutable(), startUrl, Port, ProfileDir, false, ct))
                return $"Could not start the browser: {Browser.LastError ?? "unknown error"}";
            return null;
        }
    }
}
