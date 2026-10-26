using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Baird.Services
{
    /// <summary>One title from Netflix's search API, for the signed-in account and profile.</summary>
    public sealed record NetflixSearchResult(string Title, string TitleId, string? ImageUrl, string? Kind = null, int? MaturityLevel = null)
    {
        public string Url => $"https://www.netflix.com/title/{TitleId}";
    }

    /// <summary>
    /// Session state read from a Netflix app page. The search API also answers signed-out browsers (with the general
    /// country catalogue), so results are only "filtered by this account" once IsMember is true and no profile is pending.
    /// </summary>
    /// <param name="BackPressed">Only from <see cref="ICdpBrowserService.WatchNetflixSignInAsync"/>: the viewer pressed Back on the page.</param>
    public sealed record NetflixSessionState(string Url, bool SignedOut, bool IsMember, IReadOnlyList<string> Profiles, bool BackPressed = false);

    /// <summary>Outcome of one SearchPageQueryResults call: results, or a GraphQL error code.</summary>
    public sealed record NetflixSearchReply(IReadOnlyList<NetflixSearchResult> Results, bool HasNextPage, string? ErrorCode, string? ErrorMessage)
    {
        public bool Ok => ErrorCode == null;

        /// <summary>The stored request no longer matches Netflix's web app: re-capture it from the real search page.</summary>
        public bool NeedsNewTemplate => ErrorCode is "PERSISTED_QUERY_NOT_FOUND" or "INVALID_VARIABLES" or "ValidationError";
    }

    /// <summary>
    /// The request Netflix's own search page sends to POST /graphql (operation SearchPageQueryResults, a persisted query
    /// identified by id). Captured from the live site; Netflix can rotate the id when it ships a new web app, in which case
    /// the server answers PERSISTED_QUERY_NOT_FOUND and a fresh copy is captured from the search page and cached on disk.
    /// </summary>
    public sealed class NetflixSearchQuery
    {
        private const string ResourceName = "Baird.Resources.netflix-search-query.json";
        private readonly JsonObject _template;

        public NetflixSearchQuery(string templateJson)
        {
            _template = JsonNode.Parse(templateJson) as JsonObject ?? throw new ArgumentException("template is not a JSON object");
            if (_template["variables"] is not JsonObject || (string?)_template["operationName"] != "SearchPageQueryResults")
                throw new ArgumentException("not a SearchPageQueryResults request");
        }

        public string? PersistedQueryId => (string?)_template["extensions"]?["persistedQuery"]?["id"];

        /// <summary>The request body for <paramref name="term"/> (first page).</summary>
        public string BodyFor(string term)
        {
            var body = (JsonObject)_template.DeepClone();
            var vars = (JsonObject)body["variables"]!;
            vars["searchTerm"] = term;
            vars["endCursor"] = null;
            return body.ToJsonString();
        }

        /// <summary>Cached copy on disk if valid (written after a refresh), else the copy built into Baird.</summary>
        public static NetflixSearchQuery Load(string cachePath)
        {
            try
            {
                if (File.Exists(cachePath)) return new NetflixSearchQuery(File.ReadAllText(cachePath));
            }
            catch { /* corrupt cache: fall back to the built-in copy */ }
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException($"missing embedded resource {ResourceName}");
            using var reader = new StreamReader(stream);
            return new NetflixSearchQuery(reader.ReadToEnd());
        }

        public void Save(string cachePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var body = (JsonObject)_template.DeepClone();
            ((JsonObject)body["variables"]!)["searchTerm"] = "";
            File.WriteAllText(cachePath, body.ToJsonString());
        }
    }

    public static class NetflixSearchParser
    {
        public static string SearchUrl(string term) => "https://www.netflix.com/search?q=" + Uri.EscapeDataString(term);

        /// <summary>
        /// Parses a SearchPageQueryResults reply: data.page.sections.edges[].node of type PinotGallerySection holds the results
        /// ("Films & Series"); each entity has displayString, unifiedEntity { __typename (Show/Movie), videoId,
        /// contentAdvisory.maturityLevel } and contextualArtwork.artwork.url. "More to explore" suggestions are ignored.
        /// </summary>
        public static NetflixSearchReply Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new NetflixSearchReply(Array.Empty<NetflixSearchResult>(), false, "NO_REPLY", "no reply from Netflix");
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                {
                    var e = errors[0];
                    var ext = e.TryGetProperty("extensions", out var x) && x.ValueKind == JsonValueKind.Object ? x : default;
                    string? code = ext.ValueKind == JsonValueKind.Object
                        ? (Str(ext, "code") ?? Str(ext, "errorDetail") ?? Str(ext, "classification"))
                        : null;
                    return new NetflixSearchReply(Array.Empty<NetflixSearchResult>(), false, code ?? "GRAPHQL_ERROR", Str(e, "message"));
                }

                var results = new List<NetflixSearchResult>();
                var seen = new HashSet<string>();
                bool hasNext = false;
                if (root.TryGetProperty("data", out var data) && data.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.Object &&
                    page.TryGetProperty("sections", out var sections) && sections.TryGetProperty("edges", out var sectionEdges))
                {
                    foreach (var sectionEdge in sectionEdges.EnumerateArray())
                    {
                        if (!sectionEdge.TryGetProperty("node", out var section) || Str(section, "__typename") != "PinotGallerySection") continue;
                        if (!section.TryGetProperty("entities", out var entities)) continue;
                        if (entities.TryGetProperty("pageInfo", out var pi) && pi.ValueKind == JsonValueKind.Object &&
                            pi.TryGetProperty("hasNextPage", out var hn) && hn.ValueKind == JsonValueKind.True) hasNext = true;
                        if (!entities.TryGetProperty("edges", out var edges)) continue;
                        foreach (var edge in edges.EnumerateArray())
                        {
                            if (!edge.TryGetProperty("node", out var node)) continue;
                            var title = Str(node, "displayString");
                            if (!node.TryGetProperty("unifiedEntity", out var entity) || entity.ValueKind != JsonValueKind.Object) continue;
                            var id = entity.TryGetProperty("videoId", out var vid) && vid.ValueKind == JsonValueKind.Number ? vid.GetRawText() : Str(entity, "videoId");
                            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                            int? maturity = entity.TryGetProperty("contentAdvisory", out var ca) && ca.ValueKind == JsonValueKind.Object &&
                                            ca.TryGetProperty("maturityLevel", out var ml) && ml.ValueKind == JsonValueKind.Number ? ml.GetInt32() : null;
                            string? image = node.TryGetProperty("contextualArtwork", out var art) && art.ValueKind == JsonValueKind.Object &&
                                            art.TryGetProperty("artwork", out var a) && a.ValueKind == JsonValueKind.Object ? Str(a, "url") : null;
                            results.Add(new NetflixSearchResult(title.Trim(), id, image, Str(entity, "__typename"), maturity));
                        }
                    }
                }
                return new NetflixSearchReply(results, hasNext, null, null);
            }
            catch (JsonException ex)
            {
                return new NetflixSearchReply(Array.Empty<NetflixSearchResult>(), false, "BAD_JSON", ex.Message);
            }
        }

        private static string? Str(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
