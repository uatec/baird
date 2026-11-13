using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baird.Services
{
    /// <summary>One season of a Netflix series. <see cref="Number"/> is its place in Netflix's list (Netflix's own titles vary: "Limited Series", "Stranger Things 2").</summary>
    public sealed record NetflixSeason(string SeasonId, int Number, string Title, int EpisodeCount);

    /// <summary>One episode. <see cref="EpisodeId"/> is the video id that <c>/watch/{id}</c> plays.</summary>
    public sealed record NetflixEpisode(string EpisodeId, int Number, string Title, string? Synopsis, int RuntimeSeconds, string? ImageUrl, bool Playable);

    /// <summary>Items from one GraphQL reply, or the error code Netflix gave.</summary>
    public sealed record NetflixList<T>(IReadOnlyList<T> Items, string? ErrorCode, string? ErrorMessage)
    {
        public bool Ok => ErrorCode == null;

        /// <summary>Netflix changed its web app: the stored persisted-query ids no longer match.</summary>
        public bool NeedsNewTemplate => ErrorCode is "PERSISTED_QUERY_NOT_FOUND" or "INVALID_VARIABLES" or "ValidationError";
    }

    /// <summary>What a session call returns: the items, or why it could not get them (browser busy, not signed in, Netflix error).</summary>
    public sealed record NetflixListOutcome<T>(IReadOnlyList<T>? Items, string? Failure) { public bool Ok => Failure == null; }

    /// <summary>
    /// The two requests Netflix's own title page sends to POST /graphql to list a series (captured from the live site on the Pi,
    /// 2026-11-13): PreviewModalEpisodeSelector gives the seasons, PreviewModalEpisodeSelectorSeasonEpisodes one season's episodes.
    /// They are persisted queries; if Netflix rotates the ids the server answers PERSISTED_QUERY_NOT_FOUND and the provider
    /// falls back to the single "Play next episode" entry until the ids here are refreshed.
    /// </summary>
    public static class NetflixEpisodeQueries
    {
        public const string SeasonsQueryId = "dbc3b274-d4f9-4811-aaf1-d082d3b936f2";
        public const string EpisodesQueryId = "e0ada6cd-31f7-4520-a4a2-93f36fb7588c";
        private const int Version = 102;
        // "seasonCount" and "count" are limits (Netflix returns what exists up to them), so these are generous.
        private const int MaxSeasons = 50;
        private const int MaxEpisodes = 200;

        public static string SeasonsBody(string showId) => new JsonObject
        {
            ["operationName"] = "PreviewModalEpisodeSelector",
            ["variables"] = new JsonObject { ["showId"] = long.Parse(showId), ["seasonCount"] = MaxSeasons },
            ["extensions"] = Persisted(SeasonsQueryId),
        }.ToJsonString();

        public static string EpisodesBody(string seasonId) => new JsonObject
        {
            ["operationName"] = "PreviewModalEpisodeSelectorSeasonEpisodes",
            ["variables"] = new JsonObject
            {
                ["playbackRedirectEnabled"] = true, ["includeEntitlementLock"] = false, ["seasonId"] = long.Parse(seasonId),
                ["count"] = MaxEpisodes, ["opaqueImageFormat"] = "WEBP", ["artworkContext"] = new JsonObject(),
            },
            ["extensions"] = Persisted(EpisodesQueryId),
        }.ToJsonString();

        private static JsonObject Persisted(string id) => new() { ["persistedQuery"] = new JsonObject { ["id"] = id, ["version"] = Version } };
    }

    public static class NetflixEpisodesParser
    {
        /// <summary>data.videos[0].seasons.edges[].node → seasons, numbered by their order.</summary>
        public static NetflixList<NetflixSeason> ParseSeasons(string? json) =>
            Parse(json, video =>
            {
                var seasons = new List<NetflixSeason>();
                if (video.TryGetProperty("seasons", out var s) && s.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
                    foreach (var edge in edges.EnumerateArray())
                    {
                        if (!edge.TryGetProperty("node", out var node) || Id(node) is not { } id) continue;
                        var count = node.TryGetProperty("episodes", out var eps) && eps.TryGetProperty("totalCount", out var tc) && tc.ValueKind == JsonValueKind.Number ? tc.GetInt32() : 0;
                        seasons.Add(new NetflixSeason(id, seasons.Count + 1, Str(node, "title") ?? "", count));
                    }
                return seasons;
            });

        /// <summary>data.videos[0].episodes.edges[].node → episodes.</summary>
        public static NetflixList<NetflixEpisode> ParseEpisodes(string? json) =>
            Parse(json, video =>
            {
                var episodes = new List<NetflixEpisode>();
                if (video.TryGetProperty("episodes", out var e) && e.TryGetProperty("edges", out var edges) && edges.ValueKind == JsonValueKind.Array)
                    foreach (var edge in edges.EnumerateArray())
                    {
                        if (!edge.TryGetProperty("node", out var node) || Id(node) is not { } id) continue;
                        var number = node.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : episodes.Count + 1;
                        var runtime = Int(node, "runtimeSec") ?? Int(node, "displayRuntimeSec") ?? 0;
                        var synopsis = node.TryGetProperty("contextualSynopsis", out var cs) ? Str(cs, "text") : null;
                        var image = node.TryGetProperty("artwork", out var art) ? Str(art, "url") : null;
                        var playable = node.TryGetProperty("isPlayable", out var p) ? p.ValueKind == JsonValueKind.True
                            : !node.TryGetProperty("isAvailable", out var a) || a.ValueKind != JsonValueKind.False;
                        episodes.Add(new NetflixEpisode(id, number, Str(node, "title") ?? $"Episode {number}", synopsis, runtime, image, playable));
                    }
                return episodes;
            });

        private static NetflixList<T> Parse<T>(string? json, Func<JsonElement, List<T>> read)
        {
            if (string.IsNullOrWhiteSpace(json)) return new(Array.Empty<T>(), "NO_REPLY", "no reply from Netflix");
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                {
                    var err = errors[0];
                    var ext = err.TryGetProperty("extensions", out var x) && x.ValueKind == JsonValueKind.Object ? x : default;
                    var code = ext.ValueKind == JsonValueKind.Object ? Str(ext, "code") ?? Str(ext, "errorDetail") ?? Str(ext, "classification") : null;
                    return new(Array.Empty<T>(), code ?? "GRAPHQL_ERROR", Str(err, "message"));
                }
                if (root.TryGetProperty("data", out var data) && data.TryGetProperty("videos", out var videos) &&
                    videos.ValueKind == JsonValueKind.Array && videos.GetArrayLength() > 0 && videos[0].ValueKind == JsonValueKind.Object)
                    return new(read(videos[0]), null, null);
                return new(Array.Empty<T>(), null, null);
            }
            catch (JsonException ex) { return new(Array.Empty<T>(), "BAD_JSON", ex.Message); }
        }

        private static string? Id(JsonElement o) =>
            o.TryGetProperty("videoId", out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.ValueKind == JsonValueKind.String ? v.GetString() : null : null;

        private static int? Int(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

        private static string? Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
