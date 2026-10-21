using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>
    /// Turns an iPlayer episode page URL into a directly playable HLS URL.
    ///
    /// yt-dlp's BBC extractor broke in Oct 2026 when the BBC retired /programmes/{pid}/playlist.json
    /// (it now redirects to /error.html). The streams themselves are still served by mediaselector, so
    /// we resolve them ourselves: iBL episode -> version pid (vpid) -> mediaselector -> HLS href.
    /// The HLS URLs carry expiring auth tokens, so this must run at play time, not at listing time.
    /// </summary>
    public static class BbcIPlayerStreamResolver
    {
        private static readonly HttpClient _httpClient = CreateHttpClient();

        // iBL answers 403 to requests without a User-Agent, which HttpClient doesn't send by default.
        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (X11; Linux aarch64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            return client;
        }

        private static readonly Regex EpisodeUrl = new(
            @"^https?://(?:www\.)?bbc\.co\.uk/iplayer/episode/([a-z0-9]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool TryGetEpisodePid(string url, out string pid)
        {
            var m = EpisodeUrl.Match(url ?? "");
            pid = m.Success ? m.Groups[1].Value : "";
            return m.Success;
        }

        /// <summary>Returns the best HLS URL for the episode, or null if it can't be resolved.</summary>
        public static async Task<string?> ResolveAsync(string episodePid, CancellationToken cancellationToken = default)
        {
            var episodeJson = await _httpClient.GetStringAsync(
                $"https://ibl.api.bbci.co.uk/ibl/v1/episodes/{episodePid}", cancellationToken).ConfigureAwait(false);
            var vpid = ParseVersionPid(episodeJson);
            if (vpid == null) return null;

            var mediaJson = await _httpClient.GetStringAsync(
                $"https://open.live.bbc.co.uk/mediaselector/6/select/version/2.0/mediaset/pc/vpid/{vpid}/format/json",
                cancellationToken).ConfigureAwait(false);
            return ParseBestHlsUrl(mediaJson);
        }

        /// <summary>Picks the "original" version (not audio-described / signed), else the first.</summary>
        public static string? ParseVersionPid(string iblEpisodeJson)
        {
            using var doc = JsonDocument.Parse(iblEpisodeJson);
            if (!doc.RootElement.TryGetProperty("episodes", out var episodes) || episodes.GetArrayLength() == 0) return null;
            if (!episodes[0].TryGetProperty("versions", out var versions)) return null;

            string? first = null;
            foreach (var v in versions.EnumerateArray())
            {
                var id = v.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                first ??= id;
                if (v.TryGetProperty("kind", out var kind) && kind.GetString() == "original") return id;
            }
            return first;
        }

        /// <summary>Highest-bitrate video media, then its most-preferred (lowest priority) https HLS connection.</summary>
        public static string? ParseBestHlsUrl(string mediaselectorJson)
        {
            using var doc = JsonDocument.Parse(mediaselectorJson);
            if (!doc.RootElement.TryGetProperty("media", out var media)) return null;

            var videos = media.EnumerateArray()
                .Where(m => GetString(m, "kind") == "video")
                .OrderByDescending(m => GetInt(m, "bitrate"));

            foreach (var video in videos)
            {
                if (!video.TryGetProperty("connection", out var connections)) continue;
                var href = connections.EnumerateArray()
                    .Where(c => GetString(c, "transferFormat") == "hls" && GetString(c, "protocol") == "https")
                    .OrderBy(c => GetInt(c, "priority"))
                    .Select(c => GetString(c, "href"))
                    .FirstOrDefault(h => !string.IsNullOrEmpty(h));
                if (href != null) return href;
            }
            return null;
        }

        private static string? GetString(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        // mediaselector encodes numbers as strings ("bitrate": "8490", "priority": "10").
        private static int GetInt(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var p)) return 0;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n;
            return int.TryParse(p.GetString(), out var s) ? s : 0;
        }
    }
}
