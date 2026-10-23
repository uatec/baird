using System;
using System.Text.RegularExpressions;

namespace Baird.Services
{
    public enum DisneyPageKind
    {
        /// <summary>Could not tell (still loading, spinner, unrelated site).</summary>
        Unknown,
        /// <summary>The email / password / one-time-code login flow is showing: not signed in.</summary>
        LoginRequired,
        /// <summary>Signed in, and "Who's watching?" is waiting for a profile.</summary>
        ProfilePicker,
        /// <summary>The signed-in app shell (navigation bar) on a home, browse, entity or play page, with no video advancing yet.</summary>
        LoggedIn,
        /// <summary>A video element has a source and currentTime is past the threshold.</summary>
        VideoPlaying
    }

    /// <summary>
    /// Pure classification of what the Disney+ page currently is, from signals gathered over CDP. No I/O, so it is unit-tested.
    /// URL shapes were observed on the live site on the Pi (login: /identity/login/enter-email, /identity/login/passwordless,
    /// /identity/login/enter-password; then /profiles/loading, /select-profile, /home, /browse/..., /play/{id}); each is optionally
    /// prefixed with a locale such as /en-gb.
    /// </summary>
    public static class DisneyPageClassifier
    {
        public const double PlayingThresholdSeconds = 0.1;

        public static DisneyPageKind Classify(string? url, bool hasLoginInput, bool hasProfilePicker, bool hasVideo, double videoCurrentTime, bool hasAppNav = false)
        {
            url ??= "";
            if (!url.Contains("disneyplus.com", StringComparison.OrdinalIgnoreCase)) return DisneyPageKind.Unknown;
            var path = PathOf(url);

            if (hasLoginInput || path.Contains("/identity/")) return DisneyPageKind.LoginRequired;
            if (hasProfilePicker || path.Contains("/select-profile")) return DisneyPageKind.ProfilePicker;
            if (hasVideo && videoCurrentTime > PlayingThresholdSeconds) return DisneyPageKind.VideoPlaying;
            // The URL alone proves nothing: a signed-out visitor is first served /browse/... and only then redirected to the login
            // flow (measured on the Pi). The signed-in app shell is what counts: its navigation bar (data-testid navigation-item-*).
            if (hasAppNav && AppPath.IsMatch(path)) return DisneyPageKind.LoggedIn;
            return DisneyPageKind.Unknown;
        }

        // Optional locale prefix ("/en-gb/") before the app section.
        private static readonly Regex AppPath = new(@"^/([a-z]{2}-[a-z]{2}/)?(home|browse|play|video|entity)(/|$|\?)", RegexOptions.Compiled);

        private static string PathOf(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath.ToLowerInvariant() : url.ToLowerInvariant();
    }
}
