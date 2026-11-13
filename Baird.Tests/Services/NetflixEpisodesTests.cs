using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Baird.Services;
using Baird.Tests.ViewModels;
using Xunit;

namespace Baird.Tests.Services
{
    /// <summary>Shapes captured from Netflix's own title page (operations PreviewModalEpisodeSelector and ...SeasonEpisodes).</summary>
    internal static class NetflixEpisodeFixtures
    {
        public static string Seasons(params (long Id, string Title, int Count)[] seasons) =>
            new JsonObject
            {
                ["data"] = new JsonObject
                {
                    ["videos"] = new JsonArray(new JsonObject
                    {
                        ["__typename"] = "Show", ["videoId"] = 80057281,
                        ["seasons"] = new JsonObject
                        {
                            ["edges"] = new JsonArray(seasons.Select(s => (JsonNode)new JsonObject
                            {
                                ["node"] = new JsonObject
                                {
                                    ["__typename"] = "Season", ["videoId"] = s.Id, ["title"] = s.Title,
                                    ["episodes"] = new JsonObject { ["totalCount"] = s.Count },
                                },
                            }).ToArray()),
                        },
                    }),
                },
            }.ToJsonString();

        public static JsonObject Episode(int number, long id, string title, bool playable = true, int runtime = 2700, string? synopsis = "Synopsis.") => new()
        {
            ["node"] = new JsonObject
            {
                ["__typename"] = "Episode", ["number"] = number, ["videoId"] = id, ["title"] = title, ["runtimeSec"] = runtime,
                ["isAvailable"] = playable, ["isPlayable"] = playable,
                ["artwork"] = new JsonObject { ["url"] = $"https://img/{id}.jpg" },
                ["contextualSynopsis"] = synopsis == null ? null : new JsonObject { ["text"] = synopsis },
            },
        };

        public static string Episodes(params JsonObject[] eps) =>
            new JsonObject { ["data"] = new JsonObject { ["videos"] = new JsonArray(new JsonObject { ["episodes"] = new JsonObject { ["edges"] = new JsonArray(eps) } }) } }.ToJsonString();

        public static string Error(string code) => $"{{\"errors\":[{{\"message\":\"x\",\"extensions\":{{\"code\":\"{code}\"}}}}]}}";
    }

    public class NetflixEpisodesTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nfe-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
        private static NetflixSessionState Member() => new("https://www.netflix.com/browse", false, true, Array.Empty<string>());

        private NetflixProvider Provider(MockCdpBrowserService svc, Func<string, string?> reply) => Provider(svc, reply, out _);

        private NetflixProvider Provider(MockCdpBrowserService svc, Func<string, string?> reply, out NetflixSession session)
        {
            svc.SessionSequence = Member;
            svc.GraphQlReply = reply;
            session = new NetflixSession(svc, null) { PollInterval = TimeSpan.FromMilliseconds(5), SettleDelay = TimeSpan.FromMilliseconds(5), QueryCachePath = Path.Combine(_dir, "q.json") };
            return new NetflixProvider(session);
        }

        private static string Op(string body) => (string)JsonNode.Parse(body)!["operationName"]!;

        // ---- parsers ----
        [Fact]
        public void Seasons_parse_in_order_numbered_from_one_with_their_episode_counts()
        {
            var r = NetflixEpisodesParser.ParseSeasons(NetflixEpisodeFixtures.Seasons((80077209, "Stranger Things", 8), (80117715, "Stranger Things 2", 9)));
            Assert.True(r.Ok);
            Assert.Equal(new[] { 1, 2 }, r.Items.Select(s => s.Number));
            Assert.Equal(new[] { "80077209", "80117715" }, r.Items.Select(s => s.SeasonId));
            Assert.Equal(new[] { 8, 9 }, r.Items.Select(s => s.EpisodeCount));
        }

        [Fact]
        public void Episodes_parse_with_title_synopsis_runtime_image_and_playability()
        {
            var r = NetflixEpisodesParser.ParseEpisodes(NetflixEpisodeFixtures.Episodes(
                NetflixEpisodeFixtures.Episode(1, 80229866, "Chapter One: The Hellfire Club", runtime: 4726, synopsis: "El is bullied."),
                NetflixEpisodeFixtures.Episode(2, 80229867, "Chapter Two", playable: false, synopsis: null)));
            var e = r.Items[0];
            Assert.Equal(("80229866", 1, "Chapter One: The Hellfire Club", "El is bullied.", 4726, "https://img/80229866.jpg", true),
                (e.EpisodeId, e.Number, e.Title, e.Synopsis, e.RuntimeSeconds, e.ImageUrl, e.Playable));
            Assert.False(r.Items[1].Playable);
            Assert.Null(r.Items[1].Synopsis);
        }

        [Fact]
        public void Netflix_real_season_list_parses()
        {
            var r = NetflixEpisodesParser.ParseSeasons(NetflixRealCaptures.Seasons);
            Assert.True(r.Ok);
            Assert.Equal(new[] { 8, 9, 8, 9, 8 }, r.Items.Select(s => s.EpisodeCount));
            Assert.Equal(new[] { "Stranger Things", "Stranger Things 2", "Stranger Things 3", "Stranger Things 4", "Stranger Things 5" }, r.Items.Select(s => s.Title));
            Assert.Equal("80077209", r.Items[0].SeasonId);
        }

        [Fact]
        public void Netflix_real_episode_list_parses()
        {
            var r = NetflixEpisodesParser.ParseEpisodes(NetflixRealCaptures.SeasonOneEpisodes);
            Assert.True(r.Ok);
            Assert.Equal(Enumerable.Range(1, 8), r.Items.Select(e => e.Number));
            Assert.Equal("Chapter One: The Vanishing of Will Byers", r.Items[0].Title);
            Assert.All(r.Items, e => { Assert.True(e.Playable); Assert.True(e.RuntimeSeconds > 0); Assert.False(string.IsNullOrEmpty(e.Synopsis)); Assert.StartsWith("https://", e.ImageUrl); });
        }

        [Fact]
        public async Task Real_episodes_replace_the_placeholders_the_show_page_used_to_show()
        {
            // The reported bug: Seerr said 4 episodes, Netflix gave one "Play next episode", so the page showed
            // "1. Play next episode" and "2. Episode 2 / 3 / 4" as not available.
            var p = Provider(new MockCdpBrowserService(), _ => NetflixRealCaptures.SeasonOneEpisodes);
            var items = (await p.GetChildrenAsync("netflix:80057281|s1:80077209")).Take(4).ToList();
            var lib = new[] { new Baird.NewUi.Adapters.ShowData.LibrarySeason(1, "Season 1", items.Select(i => new Baird.ViewModels.MediaItemViewModel(i)).ToList(), "Netflix") };
            var status = new JellyseerrMediaStatus(1, new[] { new JellyseerrSeason(1, 4, 1) }, Array.Empty<JellyseerrDownload>());
            var season = Baird.NewUi.Adapters.ShowData.Merge(lib, status, "Netflix").Single();
            Assert.Equal(Baird.NewUi.Model.SeasonState.Available, season.State);
            Assert.All(season.Episodes, e => Assert.Equal(Baird.NewUi.Model.EpisodeState.Available, e.State));
            Assert.Equal("4. Chapter Four: The Body", season.Episodes[3].Label);
        }

        [Fact]
        public void A_rotated_persisted_query_is_reported_so_the_caller_can_fall_back()
        {
            var r = NetflixEpisodesParser.ParseSeasons(NetflixEpisodeFixtures.Error("PERSISTED_QUERY_NOT_FOUND"));
            Assert.False(r.Ok);
            Assert.True(r.NeedsNewTemplate);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("{\"data\":{\"videos\":[]}}")]
        public void Odd_replies_give_no_items_and_never_throw(string? json)
        {
            Assert.Empty(NetflixEpisodesParser.ParseSeasons(json).Items);
            Assert.Empty(NetflixEpisodesParser.ParseEpisodes(json).Items);
        }

        // ---- provider ----
        [Fact]
        public async Task A_series_with_several_seasons_lists_season_folders()
        {
            var p = Provider(new MockCdpBrowserService(), b => Op(b) == "PreviewModalEpisodeSelector"
                ? NetflixEpisodeFixtures.Seasons((80077209, "Stranger Things", 8), (80117715, "Stranger Things 2", 9)) : null);
            var kids = (await p.GetChildrenAsync("netflix:80057281")).ToList();
            Assert.Equal(new[] { "Season 1", "Season 2" }, kids.Select(k => k.Name));
            Assert.All(kids, k => Assert.Equal(MediaType.Brand, k.Type));
            Assert.Equal("netflix:80057281|s2:80117715", kids[1].Id);
            Assert.Equal("9 Episodes", kids[1].Details);
        }

        [Fact]
        public async Task A_season_lists_its_episodes_with_real_names_and_watch_links()
        {
            var svc = new MockCdpBrowserService();
            var p = Provider(svc, b => Op(b) == "PreviewModalEpisodeSelectorSeasonEpisodes"
                ? NetflixEpisodeFixtures.Episodes(
                    NetflixEpisodeFixtures.Episode(1, 80117716, "Chapter One: MADMAX", runtime: 3000),
                    NetflixEpisodeFixtures.Episode(2, 80117717, "Chapter Two: Trick or Treat, Freak")) : null);
            var kids = (await p.GetChildrenAsync("netflix:80057281|s2:80117715")).ToList();
            Assert.Equal(new[] { "Chapter One: MADMAX", "Chapter Two: Trick or Treat, Freak" }, kids.Select(k => k.Name));
            var e = kids[0];
            Assert.Equal("netflix:ep:80117716", e.Id);
            Assert.Equal("https://www.netflix.com/watch/80117716", e.StreamUrl);
            Assert.Equal("S2:E1", e.Details);
            Assert.Equal(MediaType.Video, e.Type);
            Assert.Equal("Netflix", e.Source);
            Assert.Equal("netflix:80057281", e.SeriesId);
            Assert.Equal(TimeSpan.FromSeconds(3000), e.Duration);
            var body = JsonNode.Parse(svc.GraphQlBodies.Single())!["variables"]!;
            Assert.Equal(80117715, (long)body["seasonId"]!);
        }

        [Fact]
        public async Task A_one_season_series_lists_its_episodes_directly()
        {
            var p = Provider(new MockCdpBrowserService(), b => Op(b) switch
            {
                "PreviewModalEpisodeSelector" => NetflixEpisodeFixtures.Seasons((80243322, "Limited Series", 7)),
                _ => NetflixEpisodeFixtures.Episodes(NetflixEpisodeFixtures.Episode(1, 80234305, "Openings")),
            });
            var kids = (await p.GetChildrenAsync("netflix:80234304")).ToList();
            Assert.Equal("Openings", kids.Single().Name);
            Assert.Equal("S1:E1", kids.Single().Details);
        }

        [Fact]
        public async Task Unplayable_episodes_are_left_out()
        {
            var p = Provider(new MockCdpBrowserService(), b => NetflixEpisodeFixtures.Episodes(
                NetflixEpisodeFixtures.Episode(1, 1, "Here"), NetflixEpisodeFixtures.Episode(2, 2, "Not yet", playable: false)));
            Assert.Equal(new[] { "Here" }, (await p.GetChildrenAsync("netflix:9|s1:5")).Select(k => k.Name));
        }

        [Fact]
        public async Task When_the_season_list_cannot_be_had_the_old_play_next_episode_entry_is_kept()
        {
            var p = Provider(new MockCdpBrowserService(), _ => NetflixEpisodeFixtures.Error("PERSISTED_QUERY_NOT_FOUND"));
            var kids = (await p.GetChildrenAsync("netflix:80057281")).ToList();
            var k = Assert.Single(kids);
            Assert.Equal("Play next episode", k.Name);
            Assert.Equal("https://www.netflix.com/watch/80057281", k.StreamUrl);
        }

        [Fact]
        public async Task While_Netflix_is_playing_the_browser_is_left_alone_and_the_old_entry_is_kept()
        {
            var svc = new MockCdpBrowserService();
            var p = Provider(svc, _ => throw new InvalidOperationException("must not call Netflix"), out var session);
            session.IsPlaybackActive = true;
            Assert.Equal("Play next episode", (await p.GetChildrenAsync("netflix:80057281")).Single().Name);
            Assert.Empty(svc.GraphQlBodies);
        }

        [Fact]
        public async Task Episode_ids_and_the_next_entry_have_no_children()
        {
            var p = Provider(new MockCdpBrowserService(), _ => throw new InvalidOperationException("must not call Netflix"));
            Assert.Empty(await p.GetChildrenAsync("netflix:ep:80117716"));
            Assert.Empty(await p.GetChildrenAsync("netflix:80057281|next"));
            Assert.Empty(await p.GetChildrenAsync("jellyfin:1"));
        }
    }
}
