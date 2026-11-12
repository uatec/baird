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
