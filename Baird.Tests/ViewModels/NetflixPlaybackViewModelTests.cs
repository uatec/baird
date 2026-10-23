using System;
using System.Threading.Tasks;
using Avalonia.Input;
using Baird.Models;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class NetflixPlaybackViewModelTests
    {
        private static MediaItemViewModel Item() => new(NetflixProvider.ToMediaItem(new NetflixSearchResult("One Piece", "80107103", null, "Show")));

        private static VideoPlaybackStatus Video(double t) => new(true, t, 4, false, 1500, "https://www.netflix.com/watch/80107103", "Netflix");

        private static (MockCdpBrowserService svc, NetflixSession session, NetflixPlaybackViewModel page) Page(Func<VideoPlaybackStatus?>? video = null)
        {
            double t = 0;
            var svc = new MockCdpBrowserService
            {
                AuthStatusToReturn = new AuthPageStatus("https://www.netflix.com/watch/80107103", "t", false, false, false, 0),
                VideoSequence = video ?? (() => Video(t += 0.4)),
                SessionSequence = () => new NetflixSessionState("https://www.netflix.com/browse", false, true, Array.Empty<string>()),
            };
            var session = new NetflixSession(svc, null) { PollInterval = TimeSpan.FromMilliseconds(5), SettleDelay = TimeSpan.FromMilliseconds(5) };
            var flow = new NetflixSpikeViewModel(svc)
            {
                PollInterval = TimeSpan.FromMilliseconds(5),
                AuthCheckTimeout = TimeSpan.FromMilliseconds(200),
                BufferingTimeout = TimeSpan.FromMilliseconds(300),
            };
            return (svc, session, new NetflixPlaybackViewModel(Item(), flow, session));
        }

        [Fact]
        public async Task Start_PlaysTheWatchUrl_AndOpensTheHudWithTheTitle()
        {
            var (svc, session, page) = Page();
            NetflixHudViewModel? hud = null;
            page.HudRequested += h => hud = h;

            await page.StartAsync();

            Assert.True(svc.LaunchCalled);
            Assert.Equal("Playing.", page.StatusText);
            Assert.NotNull(hud);
            Assert.Equal("One Piece", hud!.Title);
            Assert.True(session.IsPlaybackActive);
            Assert.False(svc.RevealWindowCalled);
        }

        [Fact]
        public async Task BackOnTheHud_ReportsProgress_FinishesAndFreesTheBrowser()
        {
            var (svc, session, page) = Page();
            NetflixHudViewModel? hud = null;
            page.HudRequested += h => hud = h;
            (TimeSpan pos, TimeSpan dur)? progress = null;
            page.ProgressReported += (p, d) => progress = (p, d);
            var finished = 0;
            page.Finished += () => finished++;
            await page.StartAsync();
            await hud!.PollAsync();

            await hud.HandleKeyAsync(Key.Escape);

            Assert.Equal(NetflixSession.IdleUrl, svc.LastNavigatedUrl);
            Assert.Equal(1, finished);
            Assert.False(session.IsPlaybackActive);
            Assert.NotNull(progress);
            Assert.Equal(TimeSpan.FromSeconds(1500), progress!.Value.dur);
            Assert.True(progress.Value.pos > TimeSpan.Zero);
        }

        [Fact]
        public async Task Failure_StaysOnThePageWithTheReason()
        {
            var (svc, session, page) = Page(video: () => null);
            var finished = 0;
            page.Finished += () => finished++;

            await page.StartAsync();

            Assert.True(page.IsProblem);
            Assert.Contains("No video playback", page.StatusText); // the flow's reason, not a simple step text
            Assert.Equal(0, finished);
            Assert.False(session.IsPlaybackActive);
        }

        [Fact]
        public async Task LeavingThePage_ReturnsTheBrowserToTheIdlePage()
        {
            var (svc, session, page) = Page();
            var finished = 0;
            page.Finished += () => finished++;
            await page.StartAsync();

            await page.CancelAsync();
            await page.CancelAsync();

            Assert.Equal(NetflixSession.IdleUrl, svc.LastNavigatedUrl);
            Assert.Equal(1, finished);
            Assert.False(session.IsPlaybackActive);
        }

        [Fact]
        public async Task WithTheBrowserAlreadyOpen_PlayNavigatesIt_AndBackReturnsItToTheIdlePage()
        {
            var (svc, session, page) = Page();
            Assert.Null(await session.EnsureReadyAsync(default)); // as at Baird's start
            svc.ResetLaunchCalled();
            NetflixHudViewModel? hud = null;
            page.HudRequested += h => hud = h;

            await page.StartAsync();
            Assert.False(svc.LaunchCalled);
            Assert.Contains("https://www.netflix.com/watch/80107103", svc.NavigatedUrls);

            await hud!.HandleKeyAsync(Key.Escape);

            Assert.False(svc.StopCalled); // stays open
            Assert.True(svc.IsRunning);
            Assert.Equal(NetflixSession.IdleUrl, svc.LastNavigatedUrl);
            Assert.False(session.IsPlaybackActive);
        }

        [Fact]
        public async Task SessionNotReady_ShowsTheReason_AndDoesNotPlay()
        {
            var (svc, session, page) = Page();
            svc.SessionSequence = () => new NetflixSessionState("https://www.netflix.com/login", true, false, Array.Empty<string>());

            await page.StartAsync();

            Assert.True(page.IsProblem);
            Assert.Contains("NETFLIX_USER", page.StatusText);
            Assert.DoesNotContain("https://www.netflix.com/watch/80107103", svc.NavigatedUrls);
            Assert.False(session.IsPlaybackActive);
        }
    }
}
