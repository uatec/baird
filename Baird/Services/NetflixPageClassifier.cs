using System;
using System.Text.RegularExpressions;

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
    /// No I/O so it can be unit-tested. The logged-out redirect, membershipStatus and sign-in link were observed on live Netflix (logged-out side only); the logged-in side and other URL patterns are heuristics.
    /// </summary>
    public static class NetflixPageClassifier
    {
        public const double PlayingThresholdSeconds = 0.1;

        public static NetflixPageKind Classify(
            string? url,
            bool hasLoginInput,
            bool hasMfaInput,
            bool hasVideo,
            double videoCurrentTime,
            string? membershipStatus = null,
            bool hasSignInLink = false)
        {
            url ??= "";
            string path = PathOf(url);

            // A password / login-id field means credentials are being asked for, whatever the URL says.
            if (hasLoginInput) return NetflixPageKind.LoginRequired;

            bool mfaUrl = path.Contains("/mfa") || path.Contains("/challenge") || path.Contains("/verify");
            if (mfaUrl || hasMfaInput) return NetflixPageKind.MfaRequired;

            bool loginUrl = path.Contains("/login") || url.Contains("authURL", StringComparison.OrdinalIgnoreCase);
            if (loginUrl) return NetflixPageKind.LoginRequired;

            bool isNetflix = url.Contains("netflix.com", StringComparison.OrdinalIgnoreCase);

            // Netflix serves logged-out visitors public pages that look like content (e.g. /watch/<id> redirects to
            // /gb/title/<id>?fromWatch=true), and sets NetflixId cookies for anonymous visitors too, so neither the URL nor
            // cookies prove a login. Observed live on a logged-out page: netflix.reactContext userInfo.membershipStatus
            // == "ANONYMOUS" and a [data-uia=header-signin-link] link.
            if (isNetflix && (string.Equals(membershipStatus, "ANONYMOUS", StringComparison.OrdinalIgnoreCase) || hasSignInLink))
                return NetflixPageKind.LoginRequired;

            if (hasVideo && videoCurrentTime > PlayingThresholdSeconds) return NetflixPageKind.VideoPlaying;

            if (isNetflix && ContentPath.IsMatch(path)) return NetflixPageKind.LoggedIn;

            return NetflixPageKind.Unknown;
        }

        // Optional country prefix ("/gb/", "/us-en/") before browse|watch|title.
        private static readonly Regex ContentPath = new(@"^/([a-z]{2}(-[a-z]{2})?/)?(browse|watch|title)(/|$|\?)", RegexOptions.Compiled);

        private static string PathOf(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var u)) return u.AbsolutePath.ToLowerInvariant();
            return url.ToLowerInvariant();
        }
    }
}
