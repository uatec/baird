using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Services;
using Baird.Tests.ViewModels;
using Xunit;

namespace Baird.Tests.Services
{
    public class NetflixProviderTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "nfp-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private static NetflixSessionState Member() => new("https://www.netflix.com/browse", false, true, Array.Empty<string>());
        private static NetflixSessionState SignedOut() => new("https://www.netflix.com/login", true, false, Array.Empty<string>());

        private static string Reply() => NetflixSearchFixtures.Reply(false,
            NetflixSearchFixtures.Entity("One Piece", 80107103, "Show"),
            NetflixSearchFixtures.Entity("One Piece Film: Red", 81700392, "Movie", img: null));

        private NetflixSession Session(MockCdpBrowserService svc, NetflixCredentials? creds = null) => new(svc, creds)
        {
            PollInterval = TimeSpan.FromMilliseconds(5),
            SettleDelay = TimeSpan.FromMilliseconds(5),
            IdleTimeout = null,
            QueryCachePath = Path.Combine(_dir, "q.json"),
        };

        [Fact]
        public async Task Search_MapsResultsToNetflixItemsThatPlayTheWatchUrl()
        {
            var svc = new MockCdpBrowserService { SessionSequence = Member, GraphQlReply = _ => Reply() };
            var provider = new NetflixProvider(Session(svc));

            var items = (await provider.SearchAsync("one piece")).ToList();

            Assert.Equal(2, items.Count);
            Assert.Equal("netflix:80107103", items[0].Id);
            Assert.Equal("One Piece", items[0].Name);
            Assert.Equal("Series", items[0].Details);
            Assert.Equal("https://www.netflix.com/watch/80107103", items[0].StreamUrl);
            Assert.Equal("Netflix", items[0].Source);
            Assert.Equal(MediaType.Video, items[0].Type);
            Assert.Equal("Film", items[1].Details);
            Assert.Equal("", items[1].ImageUrl);
        }

        [Fact]
        public async Task SecondSearch_ReusesTheOpenSignedInBrowser()
        {
            var svc = new MockCdpBrowserService { SessionSequence = Member, GraphQlReply = _ => Reply() };
            var session = Session(svc);
            var provider = new NetflixProvider(session);

            await provider.SearchAsync("one");
            svc.ResetLaunchCalled();
            var reads = 0;
            svc.SessionSequence = () => { reads++; return Member(); };
            await provider.SearchAsync("two");

            Assert.False(svc.LaunchCalled);
            Assert.Equal(0, reads);
            Assert.False(svc.StopCalled);
            Assert.Equal(2, svc.GraphQlBodies.Count);
        }

        [Fact]
        public async Task Search_WhileNetflixPlays_FailsWithoutTouchingTheBrowser()
        {
            var svc = new MockCdpBrowserService { SessionSequence = Member, GraphQlReply = _ => Reply() };
            var session = Session(svc);
            session.IsPlaybackActive = true;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new NetflixProvider(session).SearchAsync("x"));

            Assert.Contains("playing", ex.Message);
            Assert.False(svc.LaunchCalled);
            Assert.Empty(svc.GraphQlBodies);
        }

        [Fact]
        public async Task SignedOutWithoutCredentials_FailsLoudly_AndNeverReturnsTheGeneralCatalogue()
        {
            var svc = new MockCdpBrowserService { SessionSequence = SignedOut, GraphQlReply = _ => Reply() };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new NetflixProvider(Session(svc)).SearchAsync("x"));

            Assert.Contains("NETFLIX_USER", ex.Message);
            Assert.Empty(svc.GraphQlBodies);
        }

        [Fact]
        public async Task EmptyQuery_DoesNotStartTheBrowser()
        {
            var svc = new MockCdpBrowserService();

            Assert.Empty(await new NetflixProvider(Session(svc)).SearchAsync("  "));
            Assert.False(svc.LaunchCalled);
        }

        [Fact]
        public async Task IdleTimeout_StopsTheHiddenBrowser()
        {
            var svc = new MockCdpBrowserService { SessionSequence = Member, GraphQlReply = _ => Reply() };
            var session = Session(svc);
            session.IdleTimeout = TimeSpan.FromMilliseconds(50);

            await new NetflixProvider(session).SearchAsync("x");
            Assert.False(svc.StopCalled);
            for (var i = 0; i < 100 && !svc.StopCalled; i++) await Task.Delay(10);

            Assert.True(svc.StopCalled);
        }

        private static NetflixSessionState ProfileGate() => new("https://www.netflix.com/browse", false, true, new[] { "Robert", "Jeff" });

        [Fact]
        public async Task ProfileClickIgnoredWhileThePageLoads_IsRetried()
        {
            // Seen on the Pi: the first click came before Netflix's page was ready and the picker stayed.
            var svc = new MockCdpBrowserService { GraphQlReply = _ => Reply() };
            svc.SessionSequence = () => svc.ProfileSelections < 2 ? ProfileGate() : Member();

            var items = await new NetflixProvider(Session(svc)).SearchAsync("x");

            Assert.Equal(2, svc.ProfileSelections);
            Assert.NotEmpty(items);
        }

        [Fact]
        public async Task ProfilePickerThatNeverCloses_FailsWithTheReason()
        {
            var svc = new MockCdpBrowserService { SessionSequence = ProfileGate, GraphQlReply = _ => Reply() };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new NetflixProvider(Session(svc)).SearchAsync("x"));

            Assert.Contains("Who's watching", ex.Message);
            Assert.Equal(3, svc.ProfileSelections);
            Assert.Empty(svc.GraphQlBodies);
        }
    }
}
