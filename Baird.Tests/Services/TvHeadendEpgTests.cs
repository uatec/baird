using System.Text.Json;
using Baird.Models;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    public class TvHeadendEpgTests
    {
        [Fact]
        public void TvHeadendEpgGrid_DeserializesProperly()
        {
            var json = """
            {
                "totalCount": 2,
                "entries": [
                    {
                        "eventId": 1001,
                        "channelUuid": "uuid-1",
                        "channelNumber": "1",
                        "channelName": "BBC One HD",
                        "title": "EastEnders",
                        "subtitle": "Episode 5432",
                        "summary": "Drama in Albert Square.",
                        "description": "Full synopsis of the drama in Walford tonight.",
                        "start": 1700000000,
                        "stop": 1700001800
                    },
                    {
                        "eventId": 1002,
                        "channelUuid": "uuid-1",
                        "channelNumber": "1",
                        "channelName": "BBC One HD",
                        "title": "BBC News at Ten",
                        "subtitle": null,
                        "summary": "Latest national and international news.",
                        "description": "In-depth reporting from correspondents around the world.",
                        "start": 1700001800,
                        "stop": 1700003600
                    }
                ]
            }
            """;

            var grid = JsonSerializer.Deserialize(json, TvHeadendJsonContext.Default.TvHeadendEpgGrid);

            Assert.NotNull(grid);
            Assert.Equal(2, grid.TotalCount);
            Assert.Equal(2, grid.Entries.Length);

            var ev1 = grid.Entries[0];
            Assert.Equal(1001, ev1.EventId);
            Assert.Equal("uuid-1", ev1.ChannelUuid);
            Assert.Equal("1", ev1.ChannelNumber);
            Assert.Equal("BBC One HD", ev1.ChannelName);
            Assert.Equal("EastEnders", ev1.Title);
            Assert.Equal("Episode 5432", ev1.Subtitle);
            Assert.Equal("Drama in Albert Square.", ev1.Summary);
            Assert.Equal("Full synopsis of the drama in Walford tonight.", ev1.Description);
            Assert.Equal(1700000000, ev1.Start);
            Assert.Equal(1700001800, ev1.Stop);
        }

        [Fact]
        public void A_programme_image_from_the_guide_source_comes_through()
        {
            var json = """{ "entries": [ { "eventId": 7, "channelUuid": "c", "title": "Sharpe", "start": 1, "stop": 2, "image": "https://iptv-epg.org/images/x" } ] }""";
            var grid = JsonSerializer.Deserialize(json, TvHeadendJsonContext.Default.TvHeadendEpgGrid)!;
            Assert.Equal("https://iptv-epg.org/images/x", grid.Entries[0].Image);
            Assert.Equal("https://iptv-epg.org/images/x", TvHeadendService.MapToEpgProgram(grid.Entries[0], "c").ImageUrl);
            Assert.Equal("https://iptv-epg.org/images/x", Baird.NewUi.Adapters.HomeData.Map(TvHeadendService.MapToEpgProgram(grid.Entries[0], "c"))!.ImageUrl);
        }
    }
}
