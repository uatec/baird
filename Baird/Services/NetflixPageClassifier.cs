using System;

namespace Baird.Services
{
    public enum NetflixPageKind
    {
        /// <summary>Could not tell (still loading, marketing page, unrelated site).</summary>
        Unknown,
        /// <summary>A login page / credential form is showing: the user is NOT signed in.</summary>
        LoginRequired,
        /// <summary>A verification / MFA challenge is showing.</summary>
        MfaRequired,
        /// <summary>A signed-in-looking page (browse/watch/title) with no video advancing yet.</summary>
        LoggedIn,
        /// <summary>A video element exists and currentTime is past the threshold.</summary>
        VideoPlaying
    }

    /// <summary>
    /// Pure classification of what the controlled page currently is, from signals gathered via CDP.
    /// No I/O so it can be unit-tested. URL patterns are heuristics, not verified against live Netflix.
    /// </summary>
    public static class NetflixPageClassifier
    {
        public const double PlayingThresholdSeconds = 0.1;

        public static NetflixPageKind Classify(
            string? url,
            bool hasLoginInput,
            bool hasMfaInput,
            bool hasVideo,
            double videoCurrentTime)
        {
            url ??= "";
            string path = PathOf(url);

            // A password / login-id field means credentials are being asked for, whatever the URL says.
            if (hasLoginInput) return NetflixPageKind.LoginRequired;

            bool mfaUrl = path.Contains("/mfa") || path.Contains("/challenge") || path.Contains("/verify");
            if (mfaUrl || hasMfaInput) return NetflixPageKind.MfaRequired;

            bool loginUrl = path.Contains("/login") || url.Contains("authURL", StringComparison.OrdinalIgnoreCase);
            if (loginUrl) return NetflixPageKind.LoginRequired;

            if (hasVideo && videoCurrentTime > PlayingThresholdSeconds) return NetflixPageKind.VideoPlaying;

            bool isNetflix = url.Contains("netflix.com", StringComparison.OrdinalIgnoreCase);
            if (isNetflix && (path.StartsWith("/browse") || path.StartsWith("/watch") || path.StartsWith("/title")))
                return NetflixPageKind.LoggedIn;

            return NetflixPageKind.Unknown;
        }

        private static string PathOf(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u)) return u.AbsolutePath.ToLowerInvariant();
            return url.ToLowerInvariant();
        }
    }
}
