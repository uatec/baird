using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class ProgrammeArtTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "baird-art-" + Guid.NewGuid().ToString("N"));
        private string CachePath => Path.Combine(_dir, "programme-art.json");
        public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

        internal sealed class Seerr : IJellyseerrService
        {
            public readonly List<string> Queries = new();
            public readonly Dictionary<string, JellyseerrSearchResult[]> Results = new(StringComparer.OrdinalIgnoreCase);
            public Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string q, int p, CancellationToken ct = default)
            {
                Queries.Add(q);
                return Task.FromResult<IEnumerable<JellyseerrSearchResult>>(Results.TryGetValue(q, out var r) ? r : Array.Empty<JellyseerrSearchResult>());
            }
            public Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int p, CancellationToken ct = default) => Task.FromResult(Enumerable.Empty<JellyseerrSearchResult>());
            public Task<JellyseerrRequestResponse> CreateRequestAsync(int id, string t, CancellationToken ct = default) => Task.FromResult(new JellyseerrRequestResponse());
            public Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken ct = default) => Task.FromResult(Enumerable.Empty<JellyseerrRequest>());
        }

        private static Programme P(string title, string? image = null) =>
            new("p", "ch", title, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), ImageUrl: image);

        private static JellyseerrSearchResult R(string title, string type = "tv", string? backdrop = "/b.jpg", string? poster = "/p.jpg") =>
            new() { Title = title, MediaType = type, BackdropPath = backdrop, PosterPath = poster };

        [Fact]
        public async Task The_guides_own_picture_wins_and_nothing_is_looked_up()
        {
            var seerr = new Seerr();
            var art = new ProgrammeArt(seerr, CachePath);
            Assert.Equal("http://epg/x.jpg", await art.ForAsync(P("Sharpe", "http://epg/x.jpg")));
            Assert.Empty(seerr.Queries);
        }

        [Fact]
        public async Task A_title_is_matched_exactly_and_its_landscape_picture_used()
        {
            var seerr = new Seerr();
            seerr.Results["Pointless"] = new[] { R("Pointless Celebrities", backdrop: "/celeb.jpg"), R("Pointless", backdrop: "/pointless.jpg") };
            var art = new ProgrammeArt(seerr, CachePath);
            Assert.Equal("https://image.tmdb.org/t/p/w1280/pointless.jpg", await art.ForAsync(P("Pointless")));
        }

        [Fact]
        public async Task Only_a_poster_is_better_than_nothing()
        {
            var seerr = new Seerr();
            seerr.Results["The Bidding Room"] = new[] { R("The Bidding Room", backdrop: null, poster: "/bid.jpg") };
            Assert.Equal("https://image.tmdb.org/t/p/w780/bid.jpg", await new ProgrammeArt(seerr, CachePath).ForAsync(P("The Bidding Room")));
        }

        [Fact]
        public async Task Broadcast_decorations_are_ignored_when_matching()
        {
            var seerr = new Seerr();
            seerr.Results["Emmerdale"] = new[] { R("Emmerdale", backdrop: "/emm.jpg") };
            var art = new ProgrammeArt(seerr, CachePath);
            Assert.NotNull(await art.ForAsync(P("Classic Emmerdale")));
            Assert.NotNull(await art.ForAsync(P("New: Emmerdale")));
        }

        [Fact]
        public async Task A_near_miss_is_not_used()
        {
            var seerr = new Seerr();
            seerr.Results["BBC News at Six"] = new[] { R("BBC News at Ten") };
            Assert.Null(await new ProgrammeArt(seerr, CachePath).ForAsync(P("BBC News at Six")));
        }

        [Fact]
        public async Task Answers_and_misses_are_remembered_across_restarts()
        {
            var seerr = new Seerr();
            seerr.Results["Pointless"] = new[] { R("Pointless") };
            var art = new ProgrammeArt(seerr, CachePath);
            await art.ForAsync(P("Pointless"));
            await art.ForAsync(P("Nothing Like It"));
            var again = new ProgrammeArt(seerr, CachePath);
            Assert.NotNull(await again.ForAsync(P("Pointless")));
            Assert.Null(await again.ForAsync(P("Nothing Like It")));
            Assert.Equal(2, seerr.Queries.Count);
        }

        [Fact]
        public async Task On_now_cards_get_a_picture_for_whats_on_when_the_guide_has_none()
        {
            var (_, _, epg, data) = HomeDataTests.Build();
            var seerr = new Seerr();
            seerr.Results["News at Six"] = new[] { R("News at Six", backdrop: "/news.jpg") };
            var home = new HomeData(data, epg, seerr, new ProgrammeArt(seerr, CachePath));
            var card = (await home.OnNowAsync(DateTimeOffset.UtcNow)).Single(c => c.Channel?.Id == "ch1");
            Assert.Equal("https://image.tmdb.org/t/p/w1280/news.jpg", card.Programme?.ImageUrl);
        }
    }
}
