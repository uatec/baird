using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    /// <summary>Jellyfin names an episode it has no metadata for after its file: Baird shows the episode's title instead.</summary>
    public class JellyfinEpisodeNameTests
    {
        [Theory]
        [InlineData("Star Trek - Strange New Worlds - S04E02 - The Griffin Incident WEBRip", "The Griffin Incident")]
        [InlineData("Star Trek - Strange New Worlds - S04E10 - Tomorrow's Enterprise WEBRip-1080p", "Tomorrow's Enterprise")]
        [InlineData("Severance - S02E01 - Hello, Ms. Cobel Bluray-2160p Remux", "Hello, Ms. Cobel")]
        [InlineData("Show.S01E03.HDTV.x264", "Episode 3")]
        [InlineData("Strange New Worlds", "Strange New Worlds")] // a real title is left alone
        [InlineData("Level-Five Transporter Accident", "Level-Five Transporter Accident")]
        [InlineData("Spider-Man - S01E01 - The Web of Lies WEBRip", "The Web of Lies")]
        public void File_names_become_episode_titles(string name, string expected) =>
            Assert.Equal(expected, JellyfinService.EpisodeTitle(name));
    }
}

namespace Baird.Tests.Services
{
    /// <summary>Jellyfin has a description for most films; Baird used to ask for none and show a blank one.</summary>
    public class JellyfinSynopsisTests
    {
        [Fact]
        public void A_jellyfin_reply_carries_the_overview()
        {
            const string json = "{\"Items\":[{\"Name\":\"Alien\",\"Id\":\"a1\",\"Type\":\"Movie\",\"ProductionYear\":1979,\"Overview\":\"In space.\"}],\"TotalRecordCount\":1}";
            var r = System.Text.Json.JsonSerializer.Deserialize(json, AppJsonContext.Default.MovieQueryResult)!;
            Assert.Equal("In space.", r.Items[0].Overview);
            Assert.Equal("In space.", JellyfinService.SynopsisOf(r.Items[0]));
        }

        [Fact]
        public void No_overview_is_an_empty_synopsis() =>
            Assert.Equal("", JellyfinService.SynopsisOf(new MovieItem { Name = "x", Id = "1", Type = "Movie" }));
    }
}
