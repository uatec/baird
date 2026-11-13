using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.NewUi.Adapters;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class HomeServiceRowsTests
    {
        internal sealed class Seerr : IJellyseerrService
        {
            public List<JellyseerrSearchResult> Movies = new(), Tv = new();
            public List<JellyseerrRequest> Requests = new();
            public bool MoviesThrow;
            public int Calls;
            public Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string q, int p, CancellationToken ct = default) => Task.FromResult(Enumerable.Empty<JellyseerrSearchResult>());
            public Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int p, CancellationToken ct = default) => Task.FromResult(Enumerable.Empty<JellyseerrSearchResult>());
            public Task<IEnumerable<JellyseerrSearchResult>> GetProviderTrendingAsync(int providerId, string mediaType, CancellationToken ct = default)
            {
                Interlocked.Increment(ref Calls);
                if (mediaType == "movie" && MoviesThrow) throw new InvalidOperationException("down");
                return Task.FromResult<IEnumerable<JellyseerrSearchResult>>(mediaType == "movie" ? Movies : Tv);
            }
            public Task<JellyseerrRequestResponse> CreateRequestAsync(int id, string t, CancellationToken ct = default) => Task.FromResult(new JellyseerrRequestResponse());
            public Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken ct = default) => Task.FromResult<IEnumerable<JellyseerrRequest>>(Requests);
        }

        private static JellyseerrSearchResult R(int id, string type, string title) => new() { Id = id, MediaType = type, Title = title, PosterPath = "/p.jpg", ReleaseDate = "2020-01-01" };
        private static HomeData Home(Seerr? s) => new(null!, null!, s);

        [Fact]
        public void The_five_service_rows_are_in_the_agreed_order() =>
            Assert.Equal(new[] { "Netflix", "Disney+", "ITVX", "Channel 4", "BBC iPlayer" }, HomeData.ServiceRows.Select(r => r.Title));

        [Fact]
        public async Task ServiceTrending_interleaves_movies_and_tv_capped_at_20()
        {
            var s = new Seerr
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
            var s = new Seerr { MoviesThrow = true, Tv = { R(1, "tv", "Andor") } };
            var cards = await Home(s).ServiceTrendingAsync(8);
            Assert.Equal("Andor", cards.Single().Label);
        }

        [Fact]
        public async Task ServiceTrending_is_empty_without_seerr() => Assert.Empty(await Home(null).ServiceTrendingAsync(8));

        [Fact]
        public async Task ServiceTrending_second_call_inside_lifetime_does_not_call_seerr_again()
        {
            var s = new Seerr { Movies = { R(1, "movie", "Alien") } };
            var home = Home(s);
            await home.ServiceTrendingAsync(8);
            var first = s.Calls;
            await home.ServiceTrendingAsync(8);
            Assert.Equal(first, s.Calls);
        }

        [Fact]
        public async Task ServiceTrending_does_not_cache_an_empty_answer()
        {
            var s = new Seerr();
            var home = Home(s);
            await home.ServiceTrendingAsync(8);
            s.Movies.Add(R(1, "movie", "Alien"));
            Assert.Single(await home.ServiceTrendingAsync(8));
        }

        [Fact]
        public async Task Downloads_lists_pending_and_downloading_with_a_percent_badge()
        {
            var s = new Seerr();
            s.Requests.Add(new JellyseerrRequest { Status = 2, TmdbId = 1, MediaType = "movie", Title = "Alien", DownloadProgress = 40, MediaInfoStatus = 3 });
            s.Requests.Add(new JellyseerrRequest { Status = 1, TmdbId = 2, MediaType = "tv", Title = "Andor", MediaInfoStatus = 2 });
            var cards = await Home(s).DownloadsAsync();
            Assert.Equal(new[] { "Alien", "Andor" }, cards.Select(c => c.Label));
            Assert.Equal("dl:40", cards[0].Badge);
            Assert.Equal("requested", cards[1].Badge);
            Assert.Equal("tmdb:tv:2", cards[1].Id);
        }

        [Fact]
        public async Task Downloads_skips_declined_and_available_requests()
        {
            var s = new Seerr();
            s.Requests.Add(new JellyseerrRequest { Status = 3, TmdbId = 1, MediaType = "movie", Title = "Declined" });
            s.Requests.Add(new JellyseerrRequest { Status = 2, TmdbId = 2, MediaType = "movie", Title = "Done", MediaInfoStatus = 5 });
            Assert.Empty(await Home(s).DownloadsAsync());
        }

        [Fact]
        public async Task Downloads_is_empty_without_seerr() => Assert.Empty(await Home(null).DownloadsAsync());
    }
}
