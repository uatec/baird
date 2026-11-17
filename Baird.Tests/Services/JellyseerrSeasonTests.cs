using System;
using System.Linq;
using System.Threading.Tasks;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Baird.Services;
using Baird.Tests.NewUi;
using Xunit;

namespace Baird.Tests.Services
{
    public class JellyseerrSeasonTests
    {
        [Fact]
        public void Season_details_parse_names_stills_and_synopses()
        {
            var json = "{\"id\":1,\"seasonNumber\":2,\"episodes\":[" +
                "{\"episodeNumber\":1,\"name\":\"Hello, Ms. Cobel\",\"overview\":\"Back to work.\",\"stillPath\":\"/a.jpg\"}," +
                "{\"episodeNumber\":2,\"name\":\"Goodbye, Mrs. Selvig\",\"overview\":\"\",\"stillPath\":null}]}";
            var d = JellyseerrService.ParseSeason(json);
            Assert.Equal(2, d.Number);
            Assert.Equal(2, d.Episodes.Count);
            Assert.Equal(new JellyseerrEpisode(1, "Hello, Ms. Cobel", "Back to work.", "https://image.tmdb.org/t/p/w500/a.jpg"), d.Episodes[0]);
            Assert.Equal("Goodbye, Mrs. Selvig", d.Episodes[1].Name);
            Assert.Null(d.Episodes[1].StillUrl);
            Assert.Null(d.Episodes[1].Overview);
        }

        [Fact]
        public async Task A_seerr_only_series_lists_real_episodes()
        {
            var w = CatalogueTests.Build();
            var sev = ShowData.FromCard(new HomeCard(CardKind.Series, "tmdb:tv:95396", "Severance", "2022", null, null, "get", "Seerr", PlayVia.None));
            var d = await w.Shows.DetailsAsync(sev, fresh: true);
            var e1 = d.Seasons.Single(s => s.Number == 1).Episodes[0];
            Assert.Equal("1. Good News About Hell", e1.Label);
            Assert.Equal("https://image.tmdb.org/t/p/w500/sev1.jpg", e1.ImageUrl);
            Assert.Equal("Mark starts a new job.", e1.Synopsis);
            Assert.Equal("3. Episode 3", d.Seasons.Single(s => s.Number == 1).Episodes[2].Label);
        }
    }
}
