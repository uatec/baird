using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Baird.Services.Cast
{
    /// <summary>A shared link Baird can play: the URL the player takes, which service it is, and where to start.</summary>
    public sealed record CastLink(string PlayUrl, string Service, TimeSpan Start = default);

    /// <summary>
    /// Turns a link shared from a phone into one Baird plays. The player already plays web pages (YouTube and iPlayer in mpv,
    /// Netflix, Disney+, ITVX and Channel 4 in the browser engines); this tidies what apps share (youtu.be, Shorts, Netflix
    /// title pages, tracking parameters) into those pages. A new source is a new rule here, plus its player; the phone's
    /// Shortcut never changes. Anything else on the web goes to mpv, whose yt-dlp knows a great many sites.
    /// </summary>
    public static class CastLinks
    {
        private static readonly Regex UrlInText = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>The link, or null when the text has no web link in it (apps sometimes share "Title https://…").</summary>
        public static CastLink? Resolve(string? shared)
        {
            if (string.IsNullOrWhiteSpace(shared)) return null;
            var m = UrlInText.Match(shared.Trim());
            if (!m.Success || !Uri.TryCreate(m.Value.TrimEnd('.', ',', ')', '"', '\''), UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            var host = uri.Host.ToLowerInvariant();
            if (host == "youtu.be" && YouTube(uri, host) == null) return null; // a short link with no video in it

            return YouTube(uri, host)
                ?? Netflix(uri, host)
                ?? IPlayer(uri, host)
                ?? Known(uri)
                ?? new CastLink(uri.AbsoluteUri, uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host);
        }

        private static readonly Regex VideoId = new("^[A-Za-z0-9_-]{6,20}$", RegexOptions.Compiled);

        private static CastLink? YouTube(Uri uri, string host)
        {
            string? id = null;
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (host == "youtu.be") id = segments.FirstOrDefault();
            else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com")
            {
                if (segments is ["watch"]) id = Query(uri, "v");
                else if (segments is ["shorts" or "live" or "embed" or "v", var s, ..]) id = s;
                else if (segments is ["playlist"] && Query(uri, "list") is { } list)
                    return new CastLink($"https://www.youtube.com/playlist?list={Uri.EscapeDataString(list)}", "YouTube");
            }
            else return null;
            if (id == null || !VideoId.IsMatch(id)) return host == "youtu.be" ? null : new CastLink(uri.AbsoluteUri, "YouTube");
            return new CastLink($"https://www.youtube.com/watch?v={id}", "YouTube", YouTubeStart(Query(uri, "t") ?? Query(uri, "start")));
        }

        /// <summary>YouTube's t=: "90", "90s" or "1h2m3s".</summary>
        internal static TimeSpan YouTubeStart(string? t)
        {
            if (string.IsNullOrEmpty(t)) return TimeSpan.Zero;
            if (int.TryParse(t, out var plain)) return TimeSpan.FromSeconds(Math.Max(0, plain));
            var m = Regex.Match(t, @"^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$");
            if (!m.Success) return TimeSpan.Zero;
            int Part(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
            return new TimeSpan(Part(1), Part(2), Part(3));
        }

        private static readonly Regex NetflixId = new(@"^/(?:[a-z]{2}(?:-[a-z]{2})?/)?(?:title|watch)/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static CastLink? Netflix(Uri uri, string host)
        {
            if (host is not ("netflix.com" or "www.netflix.com")) return null;
            // Title pages (what the app shares) and watch pages both carry the id; Netflix starts a series where it was left.
            var id = NetflixId.Match(uri.AbsolutePath) is { Success: true } m ? m.Groups[1].Value : Query(uri, "jbv");
            return id == null ? null : new CastLink($"https://www.netflix.com/watch/{id}", "Netflix");
        }

        private static CastLink? IPlayer(Uri uri, string host)
        {
            if (host is not ("bbc.co.uk" or "www.bbc.co.uk")) return null;
            var m = Regex.Match(uri.AbsolutePath, @"^/iplayer/episode/([a-z0-9]+)", RegexOptions.IgnoreCase);
            return m.Success ? new CastLink($"https://www.bbc.co.uk/iplayer/episode/{m.Groups[1].Value.ToLowerInvariant()}", "BBC iPlayer") : null;
        }

        /// <summary>Pages a browser engine plays as they are.</summary>
        private static CastLink? Known(Uri uri)
        {
            // The engines match the canonical host; apps share it with tracking parameters, which the pages don't need.
            var url = uri.GetLeftPart(UriPartial.Path);
            if (uri.Host.Equals("disneyplus.com", StringComparison.OrdinalIgnoreCase)) url = "https://www." + url["https://".Length..];
            if (DisneySearchParser.IsDisneyUrl(url)) return new CastLink(url, "Disney+");
            if (ItvxCatalogue.IsItvxUrl(url)) return new CastLink(url, "ITVX");
            if (Channel4Catalogue.IsChannel4Url(url)) return new CastLink(url, "Channel 4");
            return null;
        }

        private static string? Query(Uri uri, string key)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = pair.IndexOf('=');
                var k = Uri.UnescapeDataString(i < 0 ? pair : pair[..i]);
                if (k == key) return i < 0 ? "" : Uri.UnescapeDataString(pair[(i + 1)..].Replace('+', ' '));
            }
            return null;
        }
    }
}
