using System;
using System.Linq;
using System.Threading.Tasks;
using Baird.NewUi.Adapters;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class HomeServiceRowsTests
    {
        private static JellyseerrSearchResult R(int id, string type, string title) => new() { Id = id, MediaType = type, Title = title, PosterPath = "/p.jpg", ReleaseDate = "2020-01-01" };
        private static HomeData Home(HomeDataTests.Seerr? s, TimeSpan? retry = null) =>
            new(null!, null!, s) { ServiceRetryLifetime = retry ?? TimeSpan.FromMinutes(2) };

        [Fact]
        public void The_five_service_rows_are_in_the_agreed_order() =>
            Assert.Equal(new[] { "Netflix", "Disney+", "ITVX", "Channel 4", "BBC iPlayer" }, HomeData.ServiceRows.Select(r => r.Title));

        [Fact]
        public void Provider_ids_are_the_ones_Seerr_lists_for_the_uk()
        {
            // Checked against the real Seerr's watch providers (GB), 2026-11-13: ITVX is 41 (54 returns nothing).
            Assert.Equal(new[] { ("Netflix", 8), ("Disney+", 337), ("ITVX", 41), ("Channel 4", 103), ("BBC iPlayer", 38) }, HomeData.ServiceRows);
        }

        [Fact]
        public async Task ServiceTrending_interleaves_movies_and_tv_capped_at_20()
        {
            var s = new HomeDataTests.Seerr
            {
                Movies = Enumerable.Range(1, 15).Select(i => R(i, "movie", $"M{i}")).ToList(),
                Tv = Enumerable.Range(1, 15).Select(i => R(100 + i, "tv", $"T{i}")).ToList(),
            };
            var cards = await Home(s).ServiceTrendingAsync(8);
            Assert.Equal(20, cards.Count);
            Assert.Equal(new[] { "M1", "T1", "M2", "T2" }, cards.Take(4).Select(c => c.Label));
            Assert.Equal("tmdb:movie:1", cards[0].Id);
            Assert.Equal("Seerr", cards[0].Source);
            Assert.Null(cards[0].Badge);
            Assert.Equal("https://image.tmdb.org/t/p/w500/p.jpg", cards[0].ImageUrl);
        }

        [Fact]
        public async Task ServiceTrending_keeps_tv_when_the_movie_call_throws()
        {
            var s = new HomeDataTests.Seerr { MoviesThrow = true, Tv = { R(1, "tv", "Andor") } };
            Assert.Equal("Andor", (await Home(s).ServiceTrendingAsync(8)).Single().Label);
        }

        [Fact]
        public async Task ServiceTrending_is_empty_without_seerr() => Assert.Empty(await Home(null).ServiceTrendingAsync(8));

        [Fact]
        public async Task ServiceTrending_second_call_inside_lifetime_does_not_call_seerr_again()
        {
            var s = new HomeDataTests.Seerr { Movies = { R(1, "movie", "Alien") }, Tv = { R(2, "tv", "Andor") } };
            var home = Home(s);
            await home.ServiceTrendingAsync(8);
            var first = s.Calls;
            await home.ServiceTrendingAsync(8);
            Assert.Equal(first, s.Calls);
        }

        [Fact]
        public async Task ServiceTrending_remembers_an_empty_answer_briefly_so_Home_does_not_ask_again()
        {
            var s = new HomeDataTests.Seerr();
            var home = Home(s);
            await home.ServiceTrendingAsync(8);
            var first = s.Calls;
            await home.ServiceTrendingAsync(8);
            Assert.Equal(first, s.Calls);
        }

        [Fact]
        public async Task ServiceTrending_asks_again_once_the_retry_wait_is_over()
        {
            var s = new HomeDataTests.Seerr();
            var home = Home(s, TimeSpan.Zero);
            await home.ServiceTrendingAsync(8);
            s.Movies.Add(R(1, "movie", "Alien"));
            Assert.Single(await home.ServiceTrendingAsync(8));
        }

        [Fact]
        public async Task ServiceTrending_a_half_answer_is_only_kept_for_the_retry_wait_not_the_full_lifetime()
        {
            var s = new HomeDataTests.Seerr { MoviesThrow = true, Tv = { R(1, "tv", "Andor") } };
            var home = Home(s, TimeSpan.Zero);
            await home.ServiceTrendingAsync(8);
            s.MoviesThrow = false; s.Movies.Add(R(2, "movie", "Alien"));
            Assert.Equal(2, (await home.ServiceTrendingAsync(8)).Count);
        }
    }
}
