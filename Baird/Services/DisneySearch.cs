using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Baird.Services
{
    /// <summary>One title from Disney+'s search API, for the signed-in account, profile and region.</summary>
    public sealed record DisneySearchResult(string Title, string EntityId, string? ImageUrl, string? Kind = null, string? Rating = null, string? Year = null,
        double? RuntimeSeconds = null, string? Description = null)
    {
        /// <summary>Opens the title's page in the Disney+ web app.</summary>
        public string Url => DisneySearchParser.EntityUrl(EntityId);
        /// <summary>Plays it. For a movie the entity id is the playback id (observed: the page's PLAY button links to /play/{entityId}).</summary>
        public string PlayUrl => DisneySearchParser.PlayUrl(EntityId);
    }

    /// <summary>
    /// What to play for an entity, from its page: the id for <c>/play/{id}</c> (for a series, the episode Disney+ would play
    /// next for this profile), the real length in seconds when known (0 = unknown) and a label for the HUD.
    /// </summary>
    public sealed record DisneyPlayTarget(string PlayId, double RuntimeSeconds, string? Label)
    {
        public string PlayUrl => DisneySearchParser.PlayUrl(PlayId);
    }

    /// <summary>One episode on a series page. <see cref="RuntimeSeconds"/> is 0 when Disney+ gives no length.</summary>
    public sealed record DisneyEpisode(string Id, string Title, int Season, int Number, double RuntimeSeconds, string? Description, string? ImageUrl)
    {
        public string PlayUrl => DisneySearchParser.PlayUrl(Id);
    }

    /// <summary>
    /// A season. A short series has its episodes in the entity page; for a long one (The Simpsons) the page lists only the season,
    /// with <see cref="TotalCount"/> and no episodes, and each season is read on its own (<see cref="DisneySearchParser.SeasonApiUrl"/>).
    /// </summary>
    public sealed record DisneySeason(int Number, string Name, IReadOnlyList<DisneyEpisode> Episodes, string? SeasonId = null, int TotalCount = 0)
    {
        /// <summary>True when the episodes the page showed are all of them.</summary>
        public bool IsComplete => SeasonId == null || Episodes.Count >= TotalCount;

        /// <summary>The number to show: the season's own count of episodes, or what the page listed.</summary>
        public int EpisodeCount => Math.Max(TotalCount, Episodes.Count);
    }

    /// <summary>The seasons and episodes of a series, from its entity page.</summary>
    public sealed record DisneyEpisodeGuide(string SeriesTitle, IReadOnlyList<DisneySeason> Seasons)
    {
        public IEnumerable<DisneyEpisode> AllEpisodes => Seasons.SelectMany(s => s.Episodes);
    }

    /// <summary>Outcome of one search call: results, or an HTTP / API error.</summary>
    public sealed record DisneySearchReply(IReadOnlyList<DisneySearchResult> Results, int Status, string? ErrorMessage)
    {
        public bool Ok => ErrorMessage == null;
        /// <summary>The access token expired or was rejected: capture a fresh one from the web app.</summary>
        public bool NeedsNewToken => Status is 401 or 403;
    }

    public static class DisneySearchParser
    {
        public const string Locale = "en-gb";
        public const string ExplorePrefix = "https://disney.api.edge.bamgrid.com/explore/";
        public const string ApiBase = ExplorePrefix + "v1.21";
        public static string SearchPageUrl => $"https://www.disneyplus.com/{Locale}/browse/search";
        public static string EntityUrl(string id) => $"https://www.disneyplus.com/{Locale}/browse/entity-{id}";
        public static string PlayUrl(string id) => $"https://www.disneyplus.com/{Locale}/play/{id}";

        private const string FilmMarker = "baird=film";

        /// <summary>
        /// The stream URL of a search result: the entity page (a series id is not a play id, so the engine reads the page to find
        /// the episode). A film carries <c>?baird=film</c>, so that the engine can play it by its entity id if the page cannot be read.
        /// </summary>
        public static string EntityStreamUrl(string id, bool isFilm) => EntityUrl(id) + (isFilm ? "?" + FilmMarker : "");

        /// <summary>Reads the play id from a play URL ("/play/{id}"); false for any other URL.</summary>
        public static bool TryParsePlayId(string? url, out string playId)
        {
            playId = "";
            if (!IsDisneyUrl(url)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(url!, @"/play/(?<id>[0-9A-Za-z-]+)");
            if (!m.Success) return false;
            playId = m.Groups["id"].Value;
            return true;
        }

        /// <summary>True for any Disney+ page URL (what the Disney+ engine plays).</summary>
        public static bool IsDisneyUrl(string? url) => url != null && url.StartsWith("https://www.disneyplus.com/", StringComparison.Ordinal);

        /// <summary>Reads an entity stream URL (see <see cref="EntityStreamUrl"/>); false for any other URL.</summary>
        public static bool TryParseEntityUrl(string? url, out string entityId, out bool isFilm)
        {
            entityId = "";
            isFilm = false;
            if (!IsDisneyUrl(url)) return false;
            var m = System.Text.RegularExpressions.Regex.Match(url!, @"/browse/entity-(?<id>[0-9A-Za-z-]+)");
            if (!m.Success) return false;
            entityId = m.Groups["id"].Value;
            isFilm = url!.Contains("?" + FilmMarker, StringComparison.Ordinal) || url.Contains("&" + FilmMarker, StringComparison.Ordinal);
            return true;
        }
        public static string SearchApiUrl(string term) => $"{ApiBase}/search?query={Uri.EscapeDataString(term)}";
        public static string EntityApiUrl(string entityId) => $"{ApiBase}/page/entity-{Uri.EscapeDataString(entityId)}?limit=15";

        /// <summary>One season's episodes (measured: <c>limit=100</c> returns up to 100 at once; <c>offset</c> pages).</summary>
        public static string SeasonApiUrl(string seasonId, int offset = 0) =>
            $"{ApiBase}/season/{Uri.EscapeDataString(seasonId)}?limit=100" + (offset > 0 ? $"&offset={offset}" : "");

        /// <summary>
        /// Artwork URL for an image id. Format copied from the web app's own &lt;img&gt; tags (ripcut-delivery "compose").
        /// </summary>
        public static string ImageUrl(string imageId, int width = 320) =>
            $"https://disney.images.edge.bamgrid.com/ripcut-delivery/v2/variant/disney/{imageId}/compose?format=webp&width={width}";

        /// <summary>
        /// Parses GET /explore/v1.21/search: data.page.containers[].items[] where type == "view". Per item: id (entity id),
        /// visuals.title, visuals.metastringParts.{ratingInfo.rating.text, releaseYearRange.startYear},
        /// visuals.artwork.standard.tile["1.78"].imageId. Movie or series is only in the base64url "infoBlock" (urn:ds:cmp:eva:movie|series).
        /// </summary>
        public static DisneySearchReply Parse(int status, string? json)
        {
            var none = Array.Empty<DisneySearchResult>();
            if (status is < 200 or >= 300) return new DisneySearchReply(none, status, $"HTTP {status}" + (string.IsNullOrWhiteSpace(json) ? "" : ": " + Trim(json)));
            if (string.IsNullOrWhiteSpace(json)) return new DisneySearchReply(none, status, "no reply from Disney+");
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                    return new DisneySearchReply(none, status, "API error: " + Trim(errors[0].GetRawText()));

                var results = new List<DisneySearchResult>();
                var seen = new HashSet<string>();
                if (root.TryGetProperty("data", out var data) && data.TryGetProperty("page", out var page) &&
                    page.TryGetProperty("containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var container in containers.EnumerateArray())
                    {
                        if (!container.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                        foreach (var item in items.EnumerateArray())
                        {
                            if (Str(item, "type") != "view") continue;
                            var id = Str(item, "id");
                            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                            if (!item.TryGetProperty("visuals", out var v) || v.ValueKind != JsonValueKind.Object) continue;
                            var title = Str(v, "title");
                            if (string.IsNullOrWhiteSpace(title)) continue;

                            string? rating = null, year = null;
                            double? runtime = null;
                            if (v.TryGetProperty("metastringParts", out var mp) && mp.ValueKind == JsonValueKind.Object)
                            {
                                if (mp.TryGetProperty("ratingInfo", out var ri) && ri.TryGetProperty("rating", out var r)) rating = Str(r, "text");
                                if (mp.TryGetProperty("releaseYearRange", out var ry)) year = Str(ry, "startYear");
                                runtime = RuntimeSecondsOf(mp);
                            }
                            string? description = v.TryGetProperty("description", out var desc) ? Str(desc, "brief") : null;

                            string? image = null;
                            if (v.TryGetProperty("artwork", out var art) && art.TryGetProperty("standard", out var std) &&
                                std.TryGetProperty("tile", out var tile) && tile.TryGetProperty("1.78", out var t178))
                            {
                                var imageId = Str(t178, "imageId");
                                if (!string.IsNullOrEmpty(imageId)) image = ImageUrl(imageId);
                            }

                            results.Add(new DisneySearchResult(title.Trim(), id, image, KindOf(Str(item, "infoBlock")), rating, year, runtime, description));
                        }
                    }
                }
                return new DisneySearchReply(results, status, null);
            }
            catch (JsonException ex)
            {
                return new DisneySearchReply(none, status, "BAD_JSON: " + ex.Message);
            }
        }

        /// <summary>
        /// Parses GET /explore/v1.21/page/entity-{id}: data.page.actions[] has a <c>type: "playback"</c> action whose
        /// <c>deeplinkId</c> is the play id (for a series, the episode Disney+ would play next, "CONTINUE", else episode 1).
        /// The length is in <c>visuals.metastringParts.runtime.runtimeMs</c>: on the episode item
        /// (<c>containers[type == "episodes"].seasons[].items[]</c>, matched by id) for a series, on the page for a film.
        /// Returns null with the reason in <paramref name="failure"/> when the page has no playable action.
        /// </summary>
        public static DisneyPlayTarget? ParseEntityPlayTarget(int status, string? json, out string? failure)
        {
            failure = null;
            if (status is < 200 or >= 300) { failure = $"HTTP {status}" + (string.IsNullOrWhiteSpace(json) ? "" : ": " + Trim(json)); return null; }
            if (string.IsNullOrWhiteSpace(json)) { failure = "no reply from Disney+"; return null; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("page", out var page) || page.ValueKind != JsonValueKind.Object)
                {
                    failure = "the entity page has no data.page";
                    return null;
                }

                string? playId = null;
                if (page.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in actions.EnumerateArray())
                    {
                        if (Str(a, "type") == "playback" && Str(a, "deeplinkId") is { Length: > 0 } id) { playId = id; break; }
                    }
                }
                if (playId == null) { failure = "the entity page has no playback action (not available to watch for this profile?)"; return null; }

                double runtime = 0;
                string? label = null;
                var found = false;
                if (page.TryGetProperty("containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in containers.EnumerateArray())
                    {
                        if (Str(c, "type") != "episodes" || !c.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array) continue;
                        foreach (var season in seasons.EnumerateArray())
                        {
                            if (!season.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                            foreach (var item in items.EnumerateArray())
                            {
                                if (Str(item, "id") != playId || !item.TryGetProperty("visuals", out var iv)) continue;
                                if (iv.TryGetProperty("metastringParts", out var imp)) runtime = RuntimeSecondsOf(imp) ?? 0;
                                var epTitle = Str(iv, "episodeTitle");
                                var sNo = Str(iv, "seasonNumber");
                                var eNo = Str(iv, "episodeNumber");
                                var show = page.TryGetProperty("visuals", out var pv0) ? Str(pv0, "title") : null;
                                label = string.Join(" ", new[] { show is { Length: > 0 } ? show + " —" : null,
                                    sNo != null && eNo != null ? $"S{sNo}:E{eNo}" : null, epTitle }.Where(x => !string.IsNullOrWhiteSpace(x)));
                                found = true;
                                break;
                            }
                            if (found) break;
                        }
                        if (found) break;
                    }
                }
                if (!found && page.TryGetProperty("visuals", out var pv))
                {
                    if (pv.TryGetProperty("metastringParts", out var pmp)) runtime = RuntimeSecondsOf(pmp) ?? 0;
                    label = Str(pv, "title");
                }
                return new DisneyPlayTarget(playId, runtime, label);
            }
            catch (JsonException ex)
            {
                failure = "BAD_JSON: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Parses the seasons and episodes of a series from its entity page: <c>containers[type == "episodes"].seasons[]</c>, each with
        /// <c>visuals.name</c> ("Season 1") and <c>items[]</c> (id, visuals.episodeTitle / seasonNumber / episodeNumber /
        /// description.brief / artwork.standard.tile["1.78"] / metastringParts.runtime.runtimeMs). A page without episodes (a film)
        /// gives an empty guide, not a failure.
        /// </summary>
        public static DisneyEpisodeGuide? ParseEpisodeGuide(int status, string? json, out string? failure)
        {
            failure = null;
            if (status is < 200 or >= 300) { failure = $"HTTP {status}" + (string.IsNullOrWhiteSpace(json) ? "" : ": " + Trim(json)); return null; }
            if (string.IsNullOrWhiteSpace(json)) { failure = "no reply from Disney+"; return null; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("page", out var page) || page.ValueKind != JsonValueKind.Object)
                {
                    failure = "the entity page has no data.page";
                    return null;
                }
                var title = page.TryGetProperty("visuals", out var pv) ? Str(pv, "title") ?? "" : "";
                var seasons = new List<DisneySeason>();
                if (page.TryGetProperty("containers", out var containers) && containers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in containers.EnumerateArray())
                    {
                        if (Str(c, "type") != "episodes" || !c.TryGetProperty("seasons", out var seasonList) || seasonList.ValueKind != JsonValueKind.Array) continue;
                        var ordinal = 0;
                        foreach (var season in seasonList.EnumerateArray())
                        {
                            ordinal++;
                            var name = season.TryGetProperty("visuals", out var sv) ? Str(sv, "name") : null;
                            var episodes = season.TryGetProperty("items", out var items) ? ParseEpisodeItems(items, ordinal) : new List<DisneyEpisode>();
                            int total = season.TryGetProperty("pagination", out var pg) && pg.TryGetProperty("totalCount", out var tc) && tc.ValueKind == JsonValueKind.Number ? tc.GetInt32() : 0;
                            var seasonId = Str(season, "id");

                            // The number comes from the name ("Season 2"): Baird's continuous play steps to the next season with number + 1.
                            // A folder that is not a numbered season (Disney+'s "Exclusives": clips and extras) is left out.
                            int number;
                            var m = name == null ? null : System.Text.RegularExpressions.Regex.Match(name, @"^\s*Season\s+(\d+)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            if (m is { Success: true }) number = int.Parse(m.Groups[1].Value);
                            else if (name == null && episodes.Count > 0) number = episodes[0].Season;
                            else continue;

                            if (episodes.Count == 0 && !(seasonId != null && total > 0)) continue;
                            seasons.Add(new DisneySeason(number, name ?? $"Season {number}", episodes, seasonId, total));
                        }
                    }
                }
                return new DisneyEpisodeGuide(title, seasons.OrderBy(x => x.Number).ToList());
            }
            catch (JsonException ex)
            {
                failure = "BAD_JSON: " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Parses a season's own reply (<c>GET season/{id}</c>): <c>data.season.items[]</c> in the same shape as on the entity page, and
        /// <c>pagination.hasMore</c> / <c>totalCount</c>.
        /// </summary>
        public static IReadOnlyList<DisneyEpisode>? ParseSeasonEpisodes(int status, string? json, out bool hasMore, out string? failure)
        {
            hasMore = false;
            failure = null;
            if (status is < 200 or >= 300) { failure = $"HTTP {status}" + (string.IsNullOrWhiteSpace(json) ? "" : ": " + Trim(json)); return null; }
            if (string.IsNullOrWhiteSpace(json)) { failure = "no reply from Disney+"; return null; }
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("season", out var season) || season.ValueKind != JsonValueKind.Object)
                {
                    failure = "the season reply has no data.season";
                    return null;
                }
                hasMore = season.TryGetProperty("pagination", out var pg) && pg.TryGetProperty("hasMore", out var hm) && hm.ValueKind == JsonValueKind.True;
                return season.TryGetProperty("items", out var items) ? ParseEpisodeItems(items, 1) : new List<DisneyEpisode>();
            }
            catch (JsonException ex)
            {
                failure = "BAD_JSON: " + ex.Message;
                return null;
            }
        }

        /// <summary>Episodes from an <c>items[]</c> array: id, episodeTitle, seasonNumber, episodeNumber, runtime, description, artwork.</summary>
        private static List<DisneyEpisode> ParseEpisodeItems(JsonElement items, int seasonFallback)
        {
            var episodes = new List<DisneyEpisode>();
            if (items.ValueKind != JsonValueKind.Array) return episodes;
            foreach (var item in items.EnumerateArray())
            {
                var id = Str(item, "id");
                if (string.IsNullOrEmpty(id) || !item.TryGetProperty("visuals", out var v) || v.ValueKind != JsonValueKind.Object) continue;
                var epTitle = Str(v, "episodeTitle") ?? Str(v, "title") ?? "Episode";
                int.TryParse(Str(v, "seasonNumber"), out var sNo);
                int.TryParse(Str(v, "episodeNumber"), out var eNo);
                double runtime = v.TryGetProperty("metastringParts", out var mp) ? RuntimeSecondsOf(mp) ?? 0 : 0;
                string? description = v.TryGetProperty("description", out var d) ? Str(d, "brief") : null;
                string? image = null;
                if (v.TryGetProperty("artwork", out var art) && art.TryGetProperty("standard", out var std) &&
                    std.TryGetProperty("tile", out var tile) && tile.TryGetProperty("1.78", out var t178) && Str(t178, "imageId") is { Length: > 0 } imageId)
                    image = ImageUrl(imageId);
                episodes.Add(new DisneyEpisode(id, epTitle, sNo > 0 ? sNo : seasonFallback, eNo > 0 ? eNo : episodes.Count + 1, runtime, description, image));
            }
            return episodes;
        }

        /// <summary>visuals.metastringParts.runtime.runtimeMs as seconds, or null.</summary>
        private static double? RuntimeSecondsOf(JsonElement metastringParts) =>
            metastringParts.ValueKind == JsonValueKind.Object && metastringParts.TryGetProperty("runtime", out var rt) &&
            rt.ValueKind == JsonValueKind.Object && rt.TryGetProperty("runtimeMs", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.GetDouble() > 0
                ? ms.GetDouble() / 1000.0
                : null;

        /// <summary>"movie", "series" or null, read from the urn inside the base64url infoBlock.</summary>
        public static string? KindOf(string? infoBlock)
        {
            if (string.IsNullOrEmpty(infoBlock)) return null;
            try
            {
                var b64 = infoBlock.Replace('-', '+').Replace('_', '/');
                b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
                var text = Encoding.Latin1.GetString(Convert.FromBase64String(b64));
                const string marker = "urn:ds:cmp:eva:";
                var i = text.IndexOf(marker, StringComparison.Ordinal);
                if (i < 0) return null;
                var j = i + marker.Length;
                var k = j;
                while (k < text.Length && char.IsAsciiLetter(text[k])) k++;
                return k > j ? text[j..k] : null;
            }
            catch (FormatException) { return null; }
        }

        private static string Trim(string s) => s.Length <= 200 ? s : s[..200] + "…";

        private static string? Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
