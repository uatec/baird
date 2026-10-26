using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// <see cref="WatchUrl"/> is its page on www.itv.com.
    /// </summary>
    public sealed record ItvxSearchResult(ItvxKind Kind, string Id, string Title, string Description, string? ImageUrl,
        string? ParentId, string? ParentTitle, int EpisodeCount, double RuntimeSeconds)
    {
        /// <summary>The slug of the page the result lives on: its parent programme's for a special that has one, else its own.</summary>
        public string Slug => ItvxCatalogue.Slug(ParentTitle ?? Title);

        public string WatchUrl => ParentId != null ? ItvxCatalogue.WatchUrl(Slug, ParentId, Id) : ItvxCatalogue.WatchUrl(Slug, Id);
    }

    /// <summary>
    /// ITVX's catalogue over plain HTTP (no sign-in, no browser), for search:
    /// <c>GET https://textsearch.prd.oasvc.itv.com/search?...&amp;query=TERM</c>, the API the ITVX website uses. Reply:
    /// <c>results[]</c> with <c>entityType</c> (<c>programme</c>, <c>special</c>, <c>film</c>; <c>simulcast</c> for a live channel) and
    /// <c>data</c>: the id (<c>programmeCCId</c>, <c>specialCCId</c>, <c>filmCCId</c>), the title (<c>programmeTitle</c>, ...),
    /// <c>synopsis</c>, an image template (<c>imageHref</c>, for a programme <c>latestAvailableEpisode.imageHref</c>) and <c>tier</c>
    /// (<c>FREE</c>, or <c>PAID</c> for ITVX Premium). Shapes observed on 2026-10-26.
    /// <para>Programme pages (the episode lists) are NOT read here: www.itv.com answers only real browsers, so they are read in the
    /// shared browser.</para>
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
