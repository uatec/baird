using System;
using System.Threading.Tasks;
using Avalonia.Input;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class NetflixHudViewModelTests
    {
        private DateTime _now = new(2026, 10, 23, 20, 0, 0);

        private NetflixHudViewModel Hud(MockCdpBrowserService svc) => new(svc) { Now = () => _now };

        private static VideoPlaybackStatus Video(double t, bool paused = false, double duration = 3000) =>
            new(true, t, 4, paused, duration, "https://www.netflix.com/watch/1", "Netflix");

        [Fact]
        public async Task Poll_ShowsPositionDurationAndState()
        {
            var svc = new MockCdpBrowserService { VideoStatusToReturn = Video(754, paused: true) };
            var hud = Hud(svc);

            await hud.PollAsync();

            Assert.Equal("12:34", hud.PositionText);
            Assert.Equal("50:00", hud.DurationText);
            Assert.True(hud.IsPaused);
            Assert.Equal(754.0 / 3000, hud.Progress, 3);
        }

        [Fact]
        public async Task Poll_WithoutBrowser_SaysSoOnScreen()
        {
            var svc = new MockCdpBrowserService { VideoStatusToReturn = null };
            var hud = Hud(svc);

            await hud.PollAsync();

            Assert.Contains("browser", hud.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.True(hud.IsHudVisible);
        }

        [Fact]
        public async Task RightAndLeft_SeekTenSecondsThroughThePlayer()
        {
            var svc = new MockCdpBrowserService { VideoStatusToReturn = Video(100) };
            var hud = Hud(svc);
            await hud.PollAsync();

            Assert.True(await hud.HandleKeyAsync(Key.Right));
            Assert.True(await hud.HandleKeyAsync(Key.Left));
            Assert.True(await hud.HandleKeyAsync(Key.Left));

            Assert.Equal(new[] { (NetflixPlayerAction.SeekBy, 10.0), (NetflixPlayerAction.SeekBy, -10.0), (NetflixPlayerAction.SeekBy, -10.0) }, svc.PlayerActions);
            Assert.Equal("01:30", hud.PositionText); // shown at once, before the next poll
        }

        [Theory]
        [InlineData(false, NetflixPlayerAction.Pause)]
        [InlineData(true, NetflixPlayerAction.Play)]
        public async Task Ok_TogglesPlayPause(bool paused, NetflixPlayerAction expected)
        {
            var svc = new MockCdpBrowserService { VideoStatusToReturn = Video(100, paused) };
            var hud = Hud(svc);
            await hud.PollAsync();

            Assert.True(await hud.HandleKeyAsync(Key.Enter));

            Assert.Equal(expected, Assert.Single(svc.PlayerActions).Action);
            Assert.Equal(!paused, hud.IsPaused);
        }

        [Fact]
        public async Task Command_WithNoPlayer_SaysSoOnScreen()
        {
            var svc = new MockCdpBrowserService { VideoStatusToReturn = Video(100), PlayerPath = null };
            var hud = Hud(svc);
            await hud.PollAsync();

            await hud.HandleKeyAsync(Key.Space);

            Assert.Contains("player", hud.StatusText, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(Key.Escape)]
        [InlineData(Key.BrowserBack)]
        [InlineData(Key.Back)]
        public async Task Back_RequestsExit(Key key)
        {
            var hud = Hud(new MockCdpBrowserService { VideoStatusToReturn = Video(100) });
            var exits = 0;
            hud.ExitRequested += () => exits++;

            Assert.True(await hud.HandleKeyAsync(key));

            Assert.Equal(1, exits);
        }

        [Fact]
        public async Task Hud_HidesAfterTimeoutWhilePlaying_ButStaysWhilePaused()
        {
            var svc = new MockCdpBrowserService { VideoStatusToReturn = Video(100) };
            var hud = Hud(svc);
            await hud.PollAsync();
            Assert.True(hud.IsHudVisible);

            _now += hud.HudTimeout + TimeSpan.FromSeconds(1);
            await hud.PollAsync();
            Assert.False(hud.IsHudVisible);

            await hud.HandleKeyAsync(Key.Enter); // pause
            Assert.True(hud.IsHudVisible);
            svc.VideoStatusToReturn = Video(100, paused: true);
            _now += hud.HudTimeout + TimeSpan.FromSeconds(1);
            await hud.PollAsync();
            Assert.True(hud.IsHudVisible);
        }

        [Fact]
        public async Task UnknownKey_IsNotHandled_ButShowsTheHud()
        {
            var svc = new MockCdpBrowserService { VideoStatusToReturn = Video(100) };
            var hud = Hud(svc);
            await hud.PollAsync();
            _now += hud.HudTimeout + TimeSpan.FromSeconds(1);
            await hud.PollAsync();

            Assert.False(await hud.HandleKeyAsync(Key.Up));
            Assert.True(hud.IsHudVisible);
            Assert.Empty(svc.PlayerActions);
        }
    }
}
