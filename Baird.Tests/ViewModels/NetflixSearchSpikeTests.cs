using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class NetflixSearchParserTests
    {
        [Theory]
        [InlineData("/search?q=one%20piece&jbv=80107103", "80107103")]
        [InlineData("https://www.netflix.com/title/81288983?trkid=1", "81288983")]
        [InlineData("/watch/80107105", "80107105")]
        [InlineData("/search?q=x", null)]
        [InlineData("", null)]
        public void ParsesTitleIds(string href, string? expected) => Assert.Equal(expected, NetflixSearchParser.ParseTitleId(href));

        [Fact]
        public void ToResults_DropsUnreadableCardsAndDuplicates()
        {
            var results = NetflixSearchParser.ToResults(new[]
            {
                new NetflixSearchCard("One Piece", "/search?q=op&jbv=80107103", "https://img/1.jpg"),
                new NetflixSearchCard("One Piece", "/search?q=op&jbv=80107103", "https://img/1.jpg"), // repeated row
                new NetflixSearchCard("", "/search?q=op&jbv=1", null),                                // no title
                new NetflixSearchCard("No id", "/search?q=op", null),                                  // no id
                new NetflixSearchCard("One Piece Stampede", "/search?q=op&jbv=81240764", ""),
            });

            Assert.Equal(new[] { "80107103", "81240764" }, results.Select(r => r.TitleId));
            Assert.Null(results[1].ImageUrl);
            Assert.Equal("https://www.netflix.com/title/80107103", results[0].Url);
        }

        [Fact]
        public void SearchUrl_EscapesTheTerm() =>
            Assert.Equal("https://www.netflix.com/search?q=one%20piece%20%26%20co", NetflixSearchParser.SearchUrl("one piece & co"));
    }

    public class NetflixSearchSpikeViewModelTests
    {
        private static NetflixSearchPage Page(bool signedOut = false, string[]? profiles = null, int cards = 0) =>
            new("https://www.netflix.com/search?q=One%20Piece", signedOut, profiles ?? Array.Empty<string>(),
                Enumerable.Range(1, cards).Select(i => new NetflixSearchCard($"Title {i}", $"/search?q=x&jbv={1000 + i}", $"https://img/{i}.jpg")).ToList());

        private static NetflixSearchSpikeViewModel Vm(MockCdpBrowserService svc, NetflixCredentials? creds = null, string? profile = null) =>
            new(svc, creds, profile) { PollInterval = TimeSpan.FromMilliseconds(5), SettleDelay = TimeSpan.FromMilliseconds(5), Timeout = TimeSpan.FromSeconds(5) };

        [Fact]
        public async Task ProfilePicker_ThenResults_AreReadOnceStable()
        {
            var pages = new Queue<NetflixSearchPage>(new[]
            {
                Page(profiles: new[] { "Robert", "Jeff" }),
                Page(cards: 3), Page(cards: 6), Page(cards: 6), Page(cards: 6),
            });
            var svc = new MockCdpBrowserService { SearchPageSequence = () => pages.Count > 1 ? pages.Dequeue() : pages.Peek() };
            var vm = Vm(svc, profile: "Jeff");

            await vm.SearchAsync();

            Assert.True(vm.Succeeded, vm.Status);
            Assert.Equal(6, vm.Results.Count);
            Assert.Equal("Jeff", svc.LastProfileRequested);
            Assert.Equal(1, svc.ProfileSelections);
            Assert.True(svc.StopCalled); // the browser is always closed afterwards
            Assert.Contains("Found 6 titles", vm.Status);
        }

        [Fact]
        public async Task SignedOut_WithoutCredentials_FailsWithHowToFix()
        {
            var svc = new MockCdpBrowserService { SearchPageSequence = () => Page(signedOut: true) };
            var vm = Vm(svc);

            await vm.SearchAsync();

            Assert.False(vm.Succeeded);
            Assert.Contains("NETFLIX_USER", vm.Status);
            Assert.Equal(0, svc.SubmitCredentialsCalls);
            Assert.True(svc.StopCalled);
        }

        [Fact]
        public async Task SignedOut_WithCredentials_SignsInOnceThenSearches()
        {
            int reads = 0;
            var svc = new MockCdpBrowserService { SearchPageSequence = () => ++reads <= 1 ? Page(signedOut: true) : Page(cards: 4) };
            var vm = Vm(svc, new NetflixCredentials("me@example.com", "pw-not-logged"));

            await vm.SearchAsync();

            Assert.True(vm.Succeeded, vm.Status);
            Assert.Equal(1, svc.SubmitCredentialsCalls);
            Assert.Equal(4, vm.Results.Count);
            Assert.DoesNotContain(vm.LogEntries, l => l.Contains("pw-not-logged"));
        }

        [Fact]
        public async Task BrowserFailsToStart_ReportsIt()
        {
            var svc = new MockCdpBrowserService { LaunchResult = false, LastError = "profile in use" };
            var vm = Vm(svc);

            await vm.SearchAsync();

            Assert.False(vm.Succeeded);
            Assert.Contains("profile in use", vm.Status);
        }
    }
}
