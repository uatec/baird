using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    public enum ItvxKind { Programme, Special, Film }

    /// <summary>
    /// One search result: a programme (it has episodes), a special (one programme, sometimes under a parent programme) or a film.
    /// <see cref="WatchUrl"/> is its own page on www.itv.com, for a special too (measured on the Pi: the parent's page with the
    /// special's id falls back to the parent's page). A wrong slug is redirected to the right one.
    /// </summary>
    public sealed record ItvxSearchResult(ItvxKind Kind, string Id, string Title, string Description, string? ImageUrl,
        string? ParentId, string? ParentTitle, int EpisodeCount, double RuntimeSeconds)
    {
        public string Slug => ItvxCatalogue.Slug(Title);

        public string WatchUrl => ItvxCatalogue.WatchUrl(Slug, Id);
    }

    /// <summary>One episode (or the film or special) on a programme page.</summary>
    /// <param name="Id">The episode's id in its page URL (<c>encodedEpisodeId.letterA</c>, e.g. "1a7314a0001").</param>
    /// <param name="Series">ITVX's series key: a number ("1") or a word ("others" for a film or special).</param>
    public sealed record ItvxEpisode(string Id, string Ccid, string Slug, string ProgrammeCcid, string Series, int Number, string Title,
        string? Description, string? ImageUrl, double RuntimeSeconds)
    {
        public string PlayUrl => ItvxCatalogue.WatchUrl(Slug, ProgrammeCcid, Id);

        /// <summary>"S1:E2", or "" when the series is not a number.</summary>
        public string Label => int.TryParse(Series, out var n) ? $"S{n}:E{Number}" : "";
    }

    /// <summary>A programme page: its series (in ITVX's order, with their labels) and the free episodes that can be watched now.</summary>
    public sealed record ItvxProgramme(string Slug, string Ccid, string Title, IReadOnlyList<(string Key, string Label)> Series,
        IReadOnlyList<ItvxEpisode> Episodes);

    /// <summary>
    /// ITVX's catalogue over plain HTTP (no sign-in, no browser), for search:
    /// <c>GET https://textsearch.prd.oasvc.itv.com/search?...&amp;query=TERM</c>, the API the ITVX website uses. Reply:
    /// <c>results[]</c> with <c>entityType</c> (<c>programme</c>, <c>special</c>, <c>film</c>; <c>simulcast</c> for a live channel) and
    /// <c>data</c>: the id (<c>programmeCCId</c>, <c>specialCCId</c>, <c>filmCCId</c>), the title (<c>programmeTitle</c>, ...),
    /// <c>synopsis</c>, an image template (<c>imageHref</c>, for a programme <c>latestAvailableEpisode.imageHref</c>) and <c>tier</c>
    /// (<c>FREE</c>, or <c>PAID</c> for ITVX Premium). Shapes observed on 2026-10-26.
    /// <para>Programme pages (the episode lists) are NOT fetched here: www.itv.com answers only real browsers, so
    /// <see cref="ItvxService"/> fetches them in the shared browser. <see cref="ParseProgramme"/> reads what it returns: the page's
    /// Next.js data (<c>__NEXT_DATA__ → props.pageProps</c>), <c>{programme, seriesList}</c>. <c>seriesList[]</c> has every series
    /// (<c>seriesNumber</c>, a string, "others" on a film's page; <c>seriesLabel</c>) with all its <c>titles[]</c>: <c>ccid</c>,
    /// <c>episode</c>, <c>episodeTitle</c>, <c>description</c>, <c>notFormattedDuration</c> ("PT1H29M"), <c>image</c> (a template),
    /// <c>premium</c>, <c>tier</c> and <c>encodedEpisodeId.letterA</c> (the id in the episode's URL). Measured on the Pi, 2026-10-26.</para>
    /// </summary>
    public sealed class ItvxCatalogue
    {
        public const string SiteUrl = "https://www.itv.com";
        public const string SearchApiUrl = "https://textsearch.prd.oasvc.itv.com/search";

        private readonly HttpClient _http;

        public ItvxCatalogue(HttpClient? http = null)
        {
            _http = http ?? CreateHttpClient();
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
            return http;
        }

        /// <summary>The search URL. The feature set is what Baird's browser plays (DASH + Widevine), so the results are ones it can play.</summary>
        public static string SearchUrl(string term) =>
            $"{SearchApiUrl}?broadcaster=itv&featureSet=mpeg-dash,widevine,outband-webvtt,hd,single-track&onlyFree=false&platform=dotcom&query={Uri.EscapeDataString(term)}";

        public static string WatchUrl(string slug, string programmeId, string? episodeId = null) =>
            episodeId == null ? $"{SiteUrl}/watch/{slug}/{programmeId}" : $"{SiteUrl}/watch/{slug}/{programmeId}/{episodeId}";

        /// <summary>Reads a watch URL: its slug, the programme (or film, special) id and the episode id (null for the programme's own page).</summary>
        public static bool TryParseWatchUrl(string? url, out string slug, out string id, out string? episodeId)
        {
            slug = id = "";
            episodeId = null;
            if (!IsItvxUrl(url)) return false;
            var m = WatchPage.Match(url!);
            slug = m.Groups["slug"].Value.ToLowerInvariant();
            id = m.Groups["id"].Value;
            episodeId = m.Groups["ep"].Success ? m.Groups["ep"].Value : null;
            return true;
        }

        private static readonly Regex WatchPage = new(@"^https?://(www\.)?itv\.com/watch/(?<slug>[a-z0-9-]+)/(?<id>[a-z0-9]+)(/(?<ep>[a-z0-9]+))?/?(\?.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>True for an ITVX programme or episode page (/watch/{slug}/{id}[/{episodeId}]).</summary>
        public static bool IsItvxUrl(string? url) => url != null && WatchPage.IsMatch(url) && !url.Contains("/watch/collections/", StringComparison.OrdinalIgnoreCase);

        private static readonly Regex NotSlug = new("[^a-z0-9]+", RegexOptions.Compiled);

        /// <summary>
        /// The page slug for a title: lower case, "&amp;" as "and", apostrophes dropped, anything else that is not a letter or digit
        /// as one "-" ("Hell's Kitchen" = "hells-kitchen"). ITVX finds a page by its id, so a slug that differs from ITVX's own is not fatal.
        /// </summary>
        public static string Slug(string title)
        {
            var s = title.ToLowerInvariant().Replace("&", " and ").Replace("'", "").Replace("’", "");
            return NotSlug.Replace(s, "-").Trim('-');
        }

        /// <summary>An ITVX image template with its size and options filled in (16:9 at <paramref name="width"/> px).</summary>
        public static string? ImageUrl(string? template, int width = 480)
        {
            if (string.IsNullOrWhiteSpace(template)) return null;
            return template.Replace("{width}", width.ToString(CultureInfo.InvariantCulture))
                .Replace("{height}", (width * 9 / 16).ToString(CultureInfo.InvariantCulture))
                .Replace("{quality}", "80").Replace("{blur}", "0").Replace("{bg}", "false")
                .Replace("{treatment}", "title").Replace("{fallback}", "standard");
        }

        private static readonly Regex IsoDuration = new(@"^PT(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+(?:\.\d+)?)S)?$", RegexOptions.Compiled);

        /// <summary>"PT1H23M" = 4980; 0 when there is none.</summary>
        public static double ParseIsoDuration(string? iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return 0;
            var m = IsoDuration.Match(iso.Trim());
            if (!m.Success) return 0;
            double Part(string name) => m.Groups[name].Success ? double.Parse(m.Groups[name].Value, CultureInfo.InvariantCulture) : 0;
            return Part("h") * 3600 + Part("m") * 60 + Part("s");
        }

        // ---- Search -----------------------------------------------------------------------------------------------------------

        /// <summary>Searches ITVX. Throws on a network or HTTP failure, or on a reply that is not search results.</summary>
        public async Task<IReadOnlyList<ItvxSearchResult>> SearchAsync(string term, CancellationToken ct)
        {
            using var response = await _http.GetAsync(SearchUrl(term), ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"ITVX search answered HTTP {(int)response.StatusCode}.");
            // No matches: ITVX answers 204 with no body.
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return Array.Empty<ItvxSearchResult>();
            var body = await response.Content.ReadAsStringAsync(ct);
            return ParseSearch(body) ?? throw new InvalidOperationException("ITVX search sent something that is not search results.");
        }

        /// <summary>
        /// The free programmes, specials and films in a search reply, in ITVX's order, or null when the reply is not a search reply.
        /// ITVX Premium (tier PAID) and live channels are left out.
        /// </summary>
        public static IReadOnlyList<ItvxSearchResult>? ParseSearch(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
                var list = new List<ItvxSearchResult>();
                foreach (var r in results.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) continue;
                    if ((Str(d, "tier") ?? "").Contains("PAID", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ParseResult(Str(r, "entityType"), d) is { } result) list.Add(result);
                }
                return list;
            }
            catch (JsonException) { return null; }
        }

        // ---- Programme pages --------------------------------------------------------------------------------------------------

        /// <summary>
        /// A programme from its page data (<c>{programme, seriesList}</c>), or null with the reason. ITVX Premium episodes are left out, and
        /// so is a series with nothing else.
        /// </summary>
        public static ItvxProgramme? ParseProgramme(string? json, out string? failure)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                failure = "the page sent no programme data.";
                return null;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (Obj(root, "programme") is not { } programme || !root.TryGetProperty("seriesList", out var seriesList) || seriesList.ValueKind != JsonValueKind.Array)
                {
                    failure = "the page data has no programme (programme, seriesList).";
                    return null;
                }
                var title = Str(programme, "title") ?? "";
                var ccid = Str(programme, "ccid") ?? "";
                var slug = Str(programme, "titleSlug") is { Length: > 0 } ts ? ts : Slug(title);
                var series = new List<(string, string)>();
                var episodes = new List<ItvxEpisode>();
                foreach (var s in seriesList.EnumerateArray())
                {
                    var key = Str(s, "seriesNumber") ?? (s.TryGetProperty("seriesNumber", out var sn) && sn.ValueKind == JsonValueKind.Number ? sn.GetRawText() : null);
                    if (key == null || !s.TryGetProperty("titles", out var titles) || titles.ValueKind != JsonValueKind.Array) continue;
                    var before = episodes.Count;
                    foreach (var t in titles.EnumerateArray())
                    {
                        if (t.ValueKind != JsonValueKind.Object || IsPremium(t)) continue;
                        var id = Obj(t, "encodedEpisodeId") is { } enc ? Str(enc, "letterA") : null;
                        if (string.IsNullOrEmpty(id)) continue;
                        episodes.Add(new ItvxEpisode(id, Str(t, "ccid") ?? id, slug, ccid, key, Int(t, "episode"),
                            Str(t, "episodeTitle") is { Length: > 0 } et ? et : title, Str(t, "description"), ImageUrl(Str(t, "image")),
                            ParseIsoDuration(Str(t, "notFormattedDuration"))));
                    }
                    if (episodes.Count > before) series.Add((key, Str(s, "seriesLabel") is { Length: > 0 } label ? label : $"Series {key}"));
                }
                failure = null;
                return new ItvxProgramme(slug, ccid, title, series, episodes);
            }
            catch (JsonException ex)
            {
                failure = $"the page data could not be read ({ex.Message}).";
                return null;
            }
        }

        private static bool IsPremium(JsonElement t)
        {
            if (t.TryGetProperty("premium", out var p) && p.ValueKind == JsonValueKind.True) return true;
            if (!t.TryGetProperty("tier", out var tier)) return false;
            if (tier.ValueKind == JsonValueKind.String) return tier.GetString()!.Contains("PAID", StringComparison.OrdinalIgnoreCase);
            return tier.ValueKind == JsonValueKind.Array && tier.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString()!.Contains("PAID", StringComparison.OrdinalIgnoreCase));
        }

        private static ItvxSearchResult? ParseResult(string? entityType, JsonElement d)
        {
            var synopsis = Str(d, "synopsis") ?? "";
            switch (entityType)
            {
                case "programme":
                    {
                        var latest = Obj(d, "latestAvailableEpisode");
                        return Make(ItvxKind.Programme, Str(d, "programmeCCId"), Str(d, "programmeTitle"), synopsis,
                            latest is { } l ? Str(l, "imageHref") : null, null, null, Int(d, "totalAvailableEpisodes"), 0);
                    }
                case "special":
                    {
                        var parent = Obj(d, "specialProgramme");
                        return Make(ItvxKind.Special, Str(d, "specialCCId"), Str(d, "specialTitle"), synopsis, Str(d, "imageHref"),
                            parent is { } p ? Str(p, "programmeCCId") : null, parent is { } q ? Str(q, "programmeTitle") : null, 0,
                            ParseIsoDuration(Str(d, "duration")));
                    }
                case "film":
                    return Make(ItvxKind.Film, Str(d, "filmCCId"), Str(d, "filmTitle"), synopsis, Str(d, "imageHref"), null, null, 0,
                        ParseIsoDuration(Str(d, "duration")));
                default:
                    return null; // a live channel (simulcast): Baird has those from its own tuners
            }
        }

        private static ItvxSearchResult? Make(ItvxKind kind, string? id, string? title, string synopsis, string? image, string? parentId,
            string? parentTitle, int episodes, double runtime)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) return null;
            // A parent needs both its id and its title (the title gives its page's slug).
            if (string.IsNullOrEmpty(parentId) || string.IsNullOrEmpty(parentTitle)) parentId = parentTitle = null;
            return new ItvxSearchResult(kind, id, title, synopsis, ImageUrl(image), parentId, parentTitle, episodes, runtime);
        }

        private static JsonElement? Obj(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

        private static string? Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
    }
}
