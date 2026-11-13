using Baird.Models;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class SeriesOfTests
    {
        private static MediaItemData Episode(string source, string? seriesId, string? seriesName) => new()
        {
            Id = "ep", Name = "Pilot", Details = "S1:E1", ImageUrl = "", IsLive = false, Source = source, Type = MediaType.Video,
            Synopsis = "", Subtitle = "", SeriesId = seriesId, SeriesName = seriesName,
        };

        [Fact]
        public void An_episode_names_its_show_on_the_service_it_came_from()
        {
            var t = ShowData.SeriesOf(Episode(DisneyProvider.SourceName, "disney:abc", "Andor"))!;
            Assert.Equal("Andor", t.Name);
            Assert.Equal(TitleKind.Series, t.Kind);
            var src = Assert.Single(t.Sources);
            Assert.Equal("disney:abc", src.ItemId);
            Assert.Equal(PlayVia.Browser, src.Via);
        }

        [Fact]
        public void An_item_that_does_not_name_a_show_has_none()
        {
            Assert.Null(ShowData.SeriesOf(Episode("Jellyfin: x", null, null)));
            Assert.Null(ShowData.SeriesOf(Episode("Jellyfin: x", "s", null)));
        }
    }
}
