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

    public class NetflixSearchSpikeViewModelTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nfs-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static NetflixSessionState Member() => new("https://www.netflix.com/browse", false, true, Array.Empty<string>());
        private static NetflixSessionState SignedOut() => new("https://www.netflix.com/login", true, false, Array.Empty<string>());
        private static NetflixSessionState ProfileGate() => new("https://www.netflix.com/browse", false, false, new[] { "Robert", "Jeff" });
        private static string Results(int n) =>
            NetflixSearchFixtures.Reply(false, Enumerable.Range(1, n).Select(i => NetflixSearchFixtures.Entity($"Title {i}", 1000 + i)).ToArray());

        private NetflixSearchSpikeViewModel Vm(MockCdpBrowserService svc, NetflixCredentials? creds = null, string? profile = null) =>
            new(svc, creds, profile)
            {
                PollInterval = TimeSpan.FromMilliseconds(5),
                SettleDelay = TimeSpan.FromMilliseconds(5),
                Timeout = TimeSpan.FromSeconds(5),
                QueryCachePath = Path.Combine(_dir, "netflix-search-query.json")
            };

        [Fact]
        public async Task Member_OneApiCall_ShowsResults()
        {
            var svc = new MockCdpBrowserService { SessionSequence = Member, GraphQlReply = _ => Results(3) };
            var vm = Vm(svc);

            await vm.SearchAsync();

            Assert.True(vm.Succeeded, vm.Status);
            Assert.Equal(3, vm.Results.Count);
            Assert.Single(svc.GraphQlBodies);
            Assert.Equal("One Piece", (string?)JsonNode.Parse(svc.GraphQlBodies[0])!["variables"]!["searchTerm"]);
            Assert.True(svc.StopCalled);
            Assert.Contains("Netflix API", vm.Status);
        }

        [Fact]
        public async Task ProfilePicker_IsHandledBeforeSearching()
        {
            int reads = 0;
            var svc = new MockCdpBrowserService { SessionSequence = () => ++reads == 1 ? ProfileGate() : Member(), GraphQlReply = _ => Results(2) };
            var vm = Vm(svc, profile: "Jeff");

            await vm.SearchAsync();

            Assert.True(vm.Succeeded, vm.Status);
            Assert.Equal("Jeff", svc.LastProfileRequested);
            Assert.Equal(1, svc.ProfileSelections);
        }

        [Fact]
        public async Task SignedOut_NeverSearches_WithoutCredentials()
        {
            // The API would answer a signed-out browser with the general catalogue: that must not be reported as success.
            var svc = new MockCdpBrowserService { SessionSequence = SignedOut, GraphQlReply = _ => Results(5) };
            var vm = Vm(svc);

            await vm.SearchAsync();

            Assert.False(vm.Succeeded);
            Assert.Contains("NETFLIX_USER", vm.Status);
            Assert.Empty(svc.GraphQlBodies);
        }

        [Fact]
        public async Task SignedOut_WithCredentials_SignsInThenSearches()
        {
            int reads = 0;
            var svc = new MockCdpBrowserService { SessionSequence = () => ++reads == 1 ? SignedOut() : Member(), GraphQlReply = _ => Results(4) };
            var vm = Vm(svc, new NetflixCredentials("me@example.com", "pw-not-logged"));

            await vm.SearchAsync();

            Assert.True(vm.Succeeded, vm.Status);
            Assert.Equal(1, svc.SubmitCredentialsCalls);
            Assert.DoesNotContain(vm.LogEntries, l => l.Contains("pw-not-logged"));
        }

        [Fact]
        public async Task RotatedQueryId_IsRecapturedCachedAndRetried()
        {
            var svc = new MockCdpBrowserService
            {
                SessionSequence = Member,
                GraphQlReply = body => body.Contains("fresh-id") ? Results(6) : NetflixSearchFixtures.PersistedQueryNotFound,
                CaptureResult = NetflixSearchFixtures.Template("fresh-id")
            };
            var vm = Vm(svc);

            await vm.SearchAsync();

            Assert.True(vm.Succeeded, vm.Status);
            Assert.Equal(6, vm.Results.Count);
            Assert.Equal(1, svc.CaptureCalls);
            Assert.Equal(2, svc.GraphQlBodies.Count);
            Assert.Equal("fresh-id", NetflixSearchQuery.Load(vm.QueryCachePath).PersistedQueryId); // cached for next time
        }

        [Fact]
        public async Task RotatedQueryId_AndCaptureFails_ReportsIt()
        {
            var svc = new MockCdpBrowserService { SessionSequence = Member, GraphQlReply = _ => NetflixSearchFixtures.PersistedQueryNotFound, CaptureResult = null };
            var vm = Vm(svc);

            await vm.SearchAsync();

            Assert.False(vm.Succeeded);
            Assert.Contains("PERSISTED_QUERY_NOT_FOUND", vm.Status);
            Assert.True(svc.StopCalled);
        }

        [Fact]
        public async Task BrowserFailsToStart_ReportsIt()
        {
            var svc = new MockCdpBrowserService { LaunchResult = false, LastError = "profile in use" };
            var vm = Vm(svc);

            await vm.SearchAsync();

            Assert.False(vm.Succeeded);
            Assert.Contains("profile in use", vm.Status);
        }
    }
}
