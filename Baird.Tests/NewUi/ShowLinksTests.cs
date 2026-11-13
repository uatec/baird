using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Baird.NewUi.Adapters;
using Baird.NewUi.Model;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class ShowLinksTests
    {
        private static CatalogueTitle Show(string name = "Severance", string service = "Disney+", string id = "disney:abc") =>
            new($"Series:{id}", TitleKind.Series, name, 2022, "http://img", "Work", new[] { new TitleSource(service, id, PlayVia.Browser) }, new SeerrRef(95396, "tv", 5));

        [Fact]
        public void An_episode_played_from_any_service_remembers_its_show_across_restarts()
        {
            var path = Path.Combine(Path.GetTempPath(), $"links-{Guid.NewGuid():N}.json");
            try
            {
                new ShowLinks(path).Remember("disney:ep:1", Show());
                var t = new ShowLinks(path).TitleFor("disney:ep:1")!;
                Assert.Equal("Severance", t.Name);
                Assert.Equal(95396, t.Seerr!.TmdbId);
                Assert.Equal(new TitleSource("Disney+", "disney:abc", PlayVia.Browser), t.Sources.Single());
                Assert.Null(new ShowLinks(path).TitleFor("disney:ep:2"));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Films_and_unplaceable_titles_are_not_remembered_and_only_the_newest_are_kept()
        {
            var links = new ShowLinks(null);
            links.Remember("f", Show() with { Kind = TitleKind.Film });
            links.Remember("n", Show() with { Sources = Array.Empty<TitleSource>(), Seerr = null });
            Assert.Null(links.TitleFor("f"));
            Assert.Null(links.TitleFor("n"));
            for (var i = 0; i < ShowLinks.Keep + 5; i++) links.Remember($"e{i}", Show());
            Assert.Null(links.TitleFor("e0"));
            Assert.NotNull(links.TitleFor($"e{ShowLinks.Keep + 4}"));
        }

        [Fact]
        public async Task A_remembered_show_finds_the_next_episode_on_the_service_it_was_played_from()
        {
            var w = CatalogueTests.Build();
            var r = await w.Search.SearchAsync("simpsons", CatalogueTests.Channels, DateTimeOffset.UtcNow);
            var links = new ShowLinks(null);
            links.Remember("s1e1", r.Top!.Title!);
            w.History.Add(new Baird.Models.HistoryItem { Id = "s1e1", LastPosition = TimeSpan.FromMinutes(22), Duration = TimeSpan.FromMinutes(22), LastWatched = DateTime.Now, IsFinished = true });
            var next = await w.Shows.FollowingAsync(links.TitleFor("s1e1")!);
            Assert.Equal("s1e2", next!.Id);
        }
    }
}
