using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    /// <summary>Shapes mirror the live SearchPageQueryResults reply (Oct 2026), trimmed to the fields Baird reads.</summary>
    internal static class NetflixSearchFixtures
    {
        public static JsonObject Entity(string title, long id, string kind = "Show", int? maturity = 100, string? img = "https://img/x.jpg")
        {
            var entity = new JsonObject { ["__typename"] = kind, ["videoId"] = id };
            if (maturity is { } m) entity["contentAdvisory"] = new JsonObject { ["maturityLevel"] = m };
            var node = new JsonObject { ["__typename"] = "PinotStandardBoxshotEntityTreatment", ["displayString"] = title, ["unifiedEntity"] = entity };
            if (img != null) node["contextualArtwork"] = new JsonObject { ["artwork"] = new JsonObject { ["url"] = img } };
            return new JsonObject { ["node"] = node };
        }

        public static string Reply(bool hasNext, params JsonObject[] entities)
        {
            var suggestions = new JsonObject
            {
                ["node"] = new JsonObject
                {
                    ["__typename"] = "PinotListSection",
                    ["displayString"] = "More to explore",
                    ["entities"] = new JsonObject { ["edges"] = new JsonArray(new JsonObject { ["node"] = new JsonObject { ["__typename"] = "PinotSuggestionEntityTreatment", ["displayString"] = "Suggestion" } }) }
                }
            };
            var edges = new JsonArray();
            foreach (var e in entities) edges.Add(e);
            var gallery = new JsonObject
            {
                ["node"] = new JsonObject
                {
                    ["__typename"] = "PinotGallerySection",
                    ["displayString"] = "Films & Series",
                    ["entities"] = new JsonObject { ["pageInfo"] = new JsonObject { ["endCursor"] = "NDc=", ["hasNextPage"] = hasNext }, ["edges"] = edges }
                }
            };
            return new JsonObject
            {
                ["data"] = new JsonObject { ["page"] = new JsonObject { ["__typename"] = "PinotDefaultQuerySearchPage", ["sections"] = new JsonObject { ["edges"] = new JsonArray(suggestions, gallery) } } }
            }.ToJsonString();
        }

        public const string PersistedQueryNotFound =
            "{\"errors\":[{\"message\":\"PersistedQueryNotFound\",\"extensions\":{\"code\":\"PERSISTED_QUERY_NOT_FOUND\",\"errorType\":\"FAILED_PRECONDITION\"}}],\"data\":null}";

        public static string Template(string id = "2a415b1c-836f-4ffc-8e22-99e40be322b6") => new JsonObject
        {
            ["operationName"] = "SearchPageQueryResults",
            ["variables"] = new JsonObject { ["searchTerm"] = "", ["endCursor"] = "x", ["pageSize"] = 48, ["options"] = new JsonObject() },
            ["extensions"] = new JsonObject { ["persistedQuery"] = new JsonObject { ["id"] = id, ["version"] = 102 } }
        }.ToJsonString();
    }

    public class NetflixSearchParserTests
    {
        [Fact]
        public void Parse_ReadsGalleryResults_AndIgnoresSuggestions()
        {
            var reply = NetflixSearchParser.Parse(NetflixSearchFixtures.Reply(true,
                NetflixSearchFixtures.Entity("One Piece", 80107103),
                NetflixSearchFixtures.Entity("One Piece Film: Red", 81700392, kind: "Movie", maturity: 70, img: null),
                NetflixSearchFixtures.Entity("One Piece", 80107103))); // repeated

            Assert.True(reply.Ok);
            Assert.True(reply.HasNextPage);
            Assert.Equal(new[] { "80107103", "81700392" }, reply.Results.Select(r => r.TitleId));
            Assert.Equal("Show", reply.Results[0].Kind);
            Assert.Equal(100, reply.Results[0].MaturityLevel);
            Assert.Equal("https://img/x.jpg", reply.Results[0].ImageUrl);
            Assert.Equal("Movie", reply.Results[1].Kind);
            Assert.Null(reply.Results[1].ImageUrl);
            Assert.Equal("https://www.netflix.com/title/81700392", reply.Results[1].Url);
        }

        [Fact]
        public void Parse_PersistedQueryNotFound_AsksForANewTemplate()
        {
            var reply = NetflixSearchParser.Parse(NetflixSearchFixtures.PersistedQueryNotFound);
            Assert.False(reply.Ok);
            Assert.Equal("PERSISTED_QUERY_NOT_FOUND", reply.ErrorCode);
            Assert.True(reply.NeedsNewTemplate);
        }

        [Theory]
        [InlineData(null, "NO_REPLY")]
        [InlineData("<html>oops</html>", "BAD_JSON")]
        [InlineData("""{"errors":[{"message":"boom"}]}""", "GRAPHQL_ERROR")]
        public void Parse_ReportsUnusableReplies(string? json, string code)
        {
            var reply = NetflixSearchParser.Parse(json);
            Assert.Equal(code, reply.ErrorCode);
            Assert.False(reply.NeedsNewTemplate);
        }

        [Fact]
        public void SearchUrl_EscapesTheTerm() =>
            Assert.Equal("https://www.netflix.com/search?q=one%20piece%20%26%20co", NetflixSearchParser.SearchUrl("one piece & co"));
    }

    public class NetflixSearchQueryTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nfq-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void BodyFor_SetsTheTermAndResetsPaging()
        {
            var body = JsonNode.Parse(new NetflixSearchQuery(NetflixSearchFixtures.Template()).BodyFor("the crown"))!;
            Assert.Equal("the crown", (string?)body["variables"]!["searchTerm"]);
            Assert.Null(body["variables"]!["endCursor"]);
            Assert.Equal("SearchPageQueryResults", (string?)body["operationName"]);
        }

        [Fact]
        public void Rejects_SomethingThatIsNotASearchRequest() =>
            Assert.Throws<ArgumentException>(() => new NetflixSearchQuery("""{"operationName":"Other","variables":{}}"""));

        [Fact]
        public void Load_PrefersTheCache_AndFallsBackToTheBuiltInCopy()
        {
            var cache = Path.Combine(_dir, "q.json");
            Assert.NotNull(NetflixSearchQuery.Load(cache).PersistedQueryId); // built into Baird

            new NetflixSearchQuery(NetflixSearchFixtures.Template("new-id")).Save(cache);
            Assert.Equal("new-id", NetflixSearchQuery.Load(cache).PersistedQueryId);

            File.WriteAllText(cache, "{corrupt");
            Assert.NotEqual("new-id", NetflixSearchQuery.Load(cache).PersistedQueryId); // corrupt cache ignored
        }
    }
}
