using System;
using System.Collections.Concurrent;
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
    /// <summary>One programme (brand) from Channel 4's search.</summary>
    public sealed record Channel4SearchResult(string Slug, string Title, string Description, string? ImageUrl, bool Available)
    {
        /// <summary>
        /// Channel 4's search does not say film or series. Its film descriptions start with the year in brackets ("(2007) Comedy. A
        /// flashy cop..."), its series descriptions do not (checked on 2026-10-25). A heuristic: a wrong guess still plays (a film
        /// is played by its programme page, which plays the first episode), it only skips the episode list.
        /// </summary>
        public bool LooksLikeFilm => FilmDescription.IsMatch(Description);

        private static readonly Regex FilmDescription = new(@"^\(\d{4}\)", RegexOptions.Compiled);
    }

    /// <summary>One episode (or the film) on a programme page.</summary>
    public sealed record Channel4Episode(string ProgrammeId, string Slug, int Series, int Number, string Title, string? Description,
        string? ImageUrl, double RuntimeSeconds)
    {
        public string PlayUrl => Channel4Catalogue.OnDemandUrl(Slug, ProgrammeId);
    }

    /// <summary>A programme page: its kind, series and the episodes that can be watched now.</summary>
    public sealed record Channel4Programme(string Slug, string Title, string ProgrammeType, IReadOnlyList<Channel4Episode> Episodes)
    {
        /// <summary>A film ("FM" on the page): one item, no series.</summary>
        public bool IsFilm => ProgrammeType == "FM";

        /// <summary>The series that have watchable episodes, oldest first.</summary>
        public IReadOnlyList<int> SeriesNumbers => Episodes.Select(e => e.Series).Distinct().OrderBy(n => n).ToList();
    }

    /// <summary>
    /// Channel 4's catalogue, read without signing in and without the browser (only playing needs the signed-in browser):
    /// <list type="bullet">
    /// <item>Search: <c>GET https://all4nav.channel4.com/v1/api/search?expand=default&amp;q=TERM&amp;limit=N</c>, plain JSON:
    /// <c>results[].brand</c> with <c>title</c>, <c>websafeTitle</c> (the programme's slug), <c>description</c>, <c>thumbnailUrl</c> and
    /// <c>label</c> ("No episodes available to watch on demand" for a programme that cannot be watched).</item>
    /// <item>A programme: the page <c>https://www.channel4.com/programmes/{slug}</c> carries its data as a script,
    /// <c>window.__PARAMS__ = {...}</c> (a JS object literal: JSON plus bare <c>undefined</c> values). <c>initialData.brand</c> has
    /// <c>programmeType</c> ("FM" film, "MST" series...) and <c>episodes[]</c>, every episode of every series (326 for Gogglebox):
    /// <c>programmeId</c> ("59741-001"), <c>seriesNumber</c>, <c>episodeNumber</c>, <c>title</c>, <c>summary</c>, <c>image.src</c>,
    /// <c>durationLabel</c> ("23 mins", "116 mins") and <c>hrefLink</c> (<c>/programmes/{slug}/on-demand/{programmeId}</c>; absent for an
    /// episode not out yet).</item>
    /// </list>
    /// Shapes observed on 2026-10-25; the parsers are pure and tested against them.
    /// </summary>
    public sealed class Channel4Catalogue
    {
        public const string SiteUrl = "https://www.channel4.com";
        public const string SearchApiUrl = "https://all4nav.channel4.com/v1/api/search";
        /// <summary>The label Channel 4 puts on a search result with nothing to watch.</summary>
        public const string NotAvailableLabel = "No episodes available to watch on demand";

        private readonly HttpClient _http;

        public Channel4Catalogue(HttpClient? http = null)
        {
            _http = http ?? CreateHttpClient();
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
            return http;
        }

        public static string SearchUrl(string term, int limit = 50) =>
            $"{SearchApiUrl}?expand=default&q={Uri.EscapeDataString(term)}&limit={limit}&offset=0";

        public static string ProgrammeUrl(string slug) => $"{SiteUrl}/programmes/{slug}";

        public static string OnDemandUrl(string slug, string programmeId) => $"{SiteUrl}/programmes/{slug}/on-demand/{programmeId}";

        private static readonly Regex PageUrl = new(@"^https?://(www\.)?channel4\.com/programmes/(?<slug>[a-z0-9-]+)(/on-demand/(?<pid>[0-9]+-[0-9]+))?/?(\?.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>True for a Channel 4 programme page or on-demand (play) page.</summary>
        public static bool IsChannel4Url(string? url) => url != null && PageUrl.IsMatch(url);

        /// <summary>Reads a programme or on-demand page URL: its slug, and the programme id for an on-demand page (else null).</summary>
        public static bool TryParsePageUrl(string? url, out string slug, out string? programmeId)
        {
            slug = "";
            programmeId = null;
            if (url == null) return false;
            var m = PageUrl.Match(url);
            if (!m.Success) return false;
            slug = m.Groups["slug"].Value.ToLowerInvariant();
            programmeId = m.Groups["pid"].Success ? m.Groups["pid"].Value : null;
            return true;
        }

        /// <summary>
        /// Channel 4's image service resizes on request; search thumbnails come at 120 px wide, page images full size. Both are
        /// asked for at <paramref name="width"/> px.
        /// </summary>
        public static string? ImageUrl(string? src, int width = 480)
        {
            if (string.IsNullOrWhiteSpace(src)) return null;
            var q = src.IndexOf('?');
            var path = q >= 0 ? src[..q] : src;
            return $"{path}?interpolation=progressive-bicubic&output-format=jpeg&output-quality=90&resize={width}px:*";
        }

        private static readonly Regex DurationPart = new(@"(?<n>\d+)\s*(?<u>hrs?|hours?|mins?|minutes?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>"23 mins" = 1380, "1 hr 5 mins" = 3900; 0 when there is none.</summary>
        public static double ParseDurationLabel(string? label)
        {
            if (string.IsNullOrWhiteSpace(label)) return 0;
            double seconds = 0;
            foreach (Match m in DurationPart.Matches(label))
            {
                var n = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
                seconds += m.Groups["u"].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? n * 3600 : n * 60;
            }
            return seconds;
        }

        // ---- Search -----------------------------------------------------------------------------------------------------------

        /// <summary>Searches Channel 4's programmes. Throws on a network or HTTP failure, or on a reply that is not search results.</summary>
        public async Task<IReadOnlyList<Channel4SearchResult>> SearchAsync(string term, CancellationToken ct)
        {
            using var response = await _http.GetAsync(SearchUrl(term), ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Channel 4 search answered HTTP {(int)response.StatusCode}.");
            var body = await response.Content.ReadAsStringAsync(ct);
            return ParseSearch(body) ?? throw new InvalidOperationException("Channel 4 search sent something that is not search results.");
        }

        /// <summary>The programmes in a search reply, or null when the reply is not a search reply.</summary>
        public static IReadOnlyList<Channel4SearchResult>? ParseSearch(string? body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
                var list = new List<Channel4SearchResult>();
                foreach (var r in results.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("brand", out var b) || b.ValueKind != JsonValueKind.Object) continue;
                    var slug = Str(b, "websafeTitle");
                    var title = Str(b, "title");
                    if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(title)) continue;
                    var label = Str(b, "label") ?? "";
                    list.Add(new Channel4SearchResult(slug, title, Str(b, "description") ?? "", ImageUrl(Str(b, "thumbnailUrl")),
                        Available: !label.Contains("No episodes available", StringComparison.OrdinalIgnoreCase)));
                }
                return list;
            }
            catch (JsonException) { return null; }
        }

        // ---- Programme pages --------------------------------------------------------------------------------------------------

        private readonly ConcurrentDictionary<string, (DateTime At, Channel4Programme Programme)> _programmes = new();
        private readonly ConcurrentDictionary<string, double> _runtimes = new();

        /// <summary>How long a programme page is reused before it is read again.</summary>
        public TimeSpan ProgrammeLifetime { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Reads a programme page (reused for <see cref="ProgrammeLifetime"/>). Throws on a network or HTTP failure, or when the page
        /// carries no programme data.
        /// </summary>
        public async Task<Channel4Programme> GetProgrammeAsync(string slug, CancellationToken ct)
        {
            if (_programmes.TryGetValue(slug, out var cached) && DateTime.UtcNow - cached.At < ProgrammeLifetime) return cached.Programme;
            using var response = await _http.GetAsync(ProgrammeUrl(slug), ct);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Channel 4's page for {slug} answered HTTP {(int)response.StatusCode}.");
            var html = await response.Content.ReadAsStringAsync(ct);
            var programme = ParseProgramme(html, out var failure) ?? throw new InvalidOperationException($"Channel 4's page for {slug}: {failure}");
            _programmes[slug] = (DateTime.UtcNow, programme);
            foreach (var e in programme.Episodes)
                if (e.RuntimeSeconds > 0) _runtimes[e.ProgrammeId] = e.RuntimeSeconds;
            return programme;
        }

        /// <summary>
        /// The length (from its page's "23 mins" label, so to the minute) of an episode whose programme page has been read; else 0.
        /// </summary>
        public double KnownRuntimeSeconds(string programmeId) => _runtimes.TryGetValue(programmeId, out var r) ? r : 0;

        /// <summary>The programme on a page (its watchable episodes, in series and episode order), or null with the reason.</summary>
        public static Channel4Programme? ParseProgramme(string? html, out string? failure)
        {
            var json = ExtractParamsJson(html);
            if (json == null)
            {
                failure = "the page has no programme data (window.__PARAMS__).";
                return null;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("initialData", out var data) || !data.TryGetProperty("brand", out var brand) || brand.ValueKind != JsonValueKind.Object)
                {
                    failure = "the page data has no programme (initialData.brand).";
                    return null;
                }
                var slug = Str(brand, "websafeTitle") ?? "";
                var episodes = new List<Channel4Episode>();
                if (brand.TryGetProperty("episodes", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in list.EnumerateArray())
                    {
                        var pid = Str(e, "programmeId");
                        // An episode that is not out yet has no link and no length ("Watch new episode from Fri 30 Oct").
                        if (string.IsNullOrEmpty(pid) || string.IsNullOrEmpty(Str(e, "hrefLink"))) continue;
                        var series = Int(e, "seriesNumber");
                        var number = Int(e, "episodeNumber");
                        var title = Str(e, "title") is { Length: > 0 } t ? t : Str(brand, "title") ?? pid;
                        var image = e.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object ? Str(img, "src") : null;
                        episodes.Add(new Channel4Episode(pid, slug, series, number, title, Str(e, "summary"), ImageUrl(image),
                            ParseDurationLabel(Str(e, "durationLabel"))));
                    }
                }
                failure = null;
                return new Channel4Programme(slug, Str(brand, "title") ?? slug, Str(brand, "programmeType") ?? "",
                    episodes.OrderBy(e => e.Series).ThenBy(e => e.Number).ToList());
            }
            catch (JsonException ex)
            {
                failure = $"the page data could not be read ({ex.Message}).";
                return null;
            }
        }

        /// <summary>
        /// The object literal assigned to <c>window.__PARAMS__</c>, as JSON (bare <c>undefined</c> becomes <c>null</c>), or null when the
        /// page has none. The literal is found by matching braces outside strings, so a "}" or "undefined" inside a string is left alone.
        /// </summary>
        public static string? ExtractParamsJson(string? html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            var at = html.IndexOf("__PARAMS__", StringComparison.Ordinal);
            if (at < 0) return null;
            var start = html.IndexOf('{', at);
            if (start < 0) return null;

            var sb = new StringBuilder();
            int depth = 0;
            bool inString = false;
            for (int i = start; i < html.Length; i++)
            {
                var c = html[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < html.Length) sb.Append(html[++i]);
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; sb.Append(c); continue; }
                if (c == 'u' && string.CompareOrdinal(html, i, "undefined", 0, 9) == 0 && !IsIdentChar(html, i - 1) && !IsIdentChar(html, i + 9))
                {
                    sb.Append("null");
                    i += 8;
                    continue;
                }
                sb.Append(c);
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return sb.ToString();
            }
            return null;
        }

        private static bool IsIdentChar(string s, int i) => i >= 0 && i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '$');

        private static string? Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
    }
}
