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
        public static string SearchApiUrl(string term) => $"{ApiBase}/search?query={Uri.EscapeDataString(term)}";
        public static string EntityApiUrl(string entityId) => $"{ApiBase}/page/entity-{Uri.EscapeDataString(entityId)}?limit=15";

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
