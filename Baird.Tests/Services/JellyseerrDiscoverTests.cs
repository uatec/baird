using System.Linq;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    public class JellyseerrDiscoverTests
    {
        [Fact]
        public void DiscoverUrl_movie_uses_movies_endpoint_with_gb_region_and_provider() =>
            Assert.Equal("api/v1/discover/movies?watchProviders=8&watchRegion=GB&sortBy=popularity.desc&page=1", JellyseerrService.DiscoverUrl(8, "movie"));

        [Fact]
        public void DiscoverUrl_tv_uses_tv_endpoint() =>
            Assert.Equal("api/v1/discover/tv?watchProviders=337&watchRegion=GB&sortBy=popularity.desc&page=1", JellyseerrService.DiscoverUrl(337, "tv"));

        [Fact]
        public void Parse_discover_movies_without_mediaType_uses_the_given_type()
        {
            var r = JellyseerrService.ParseSearchResults("{\"results\":[{\"id\":5,\"title\":\"Alien\",\"posterPath\":\"/p.jpg\",\"releaseDate\":\"1979-05-25\"}]}", "Discover", "movie").Single();
            Assert.Equal("movie", r.MediaType);
            Assert.Equal("Alien", r.Title);
            Assert.Equal("1979-05-25", r.ReleaseDate);
        }

        [Fact]
        public void Parse_discover_tv_without_mediaType_reads_name_and_first_air_date()
        {
            var r = JellyseerrService.ParseSearchResults("{\"results\":[{\"id\":7,\"name\":\"Andor\",\"firstAirDate\":\"2022-09-21\"}]}", "Discover", "tv").Single();
            Assert.Equal("tv", r.MediaType);
            Assert.Equal("Andor", r.Title);
            Assert.Equal("2022-09-21", r.ReleaseDate);
        }

        [Fact]
        public void A_request_lists_the_seasons_that_were_asked_for()
        {
            using var doc = System.Text.Json.JsonDocument.Parse("{\"seasons\":[{\"seasonNumber\":2,\"status\":1},{\"seasonNumber\":1,\"status\":1},{\"seasonNumber\":0}]}");
            Assert.Equal(new[] { 1, 2 }, JellyseerrService.RequestedSeasonsOf(doc.RootElement)); // sorted; specials left out
            using var movie = System.Text.Json.JsonDocument.Parse("{\"id\":1}");
            Assert.Empty(JellyseerrService.RequestedSeasonsOf(movie.RootElement));
        }

        [Fact]
        public void A_titles_media_and_request_ids_are_read_from_its_details()
        {
            using var doc = System.Text.Json.JsonDocument.Parse("{\"mediaInfo\":{\"id\":5,\"status\":3,\"requests\":[{\"id\":11},{\"id\":12}]}}");
            var (media, requests) = JellyseerrService.MediaAndRequestsOf(doc.RootElement)!.Value;
            Assert.Equal(5, media);
            Assert.Equal(new[] { 11, 12 }, requests);
            using var none = System.Text.Json.JsonDocument.Parse("{\"name\":\"x\"}");
            Assert.Null(JellyseerrService.MediaAndRequestsOf(none.RootElement));
        }
    }
}
