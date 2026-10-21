using System;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class MockCdpBrowserService : ICdpBrowserService
    {
        public bool IsRunning { get; set; }
        public string? BrowserPath { get; set; } = "/usr/bin/google-chrome-stable";
        public int ProcessId { get; set; } = 12345;
        public string? LastError { get; set; }
        public bool LaunchResult { get; set; } = true;
        public bool? LastLaunchVisible { get; private set; }
        public WidevineStatus WidevineToReturn { get; set; } = new(true, "mock");
        public Func<VideoPlaybackStatus?>? VideoSequence { get; set; }

        public bool LaunchCalled { get; private set; }
        public bool CheckAuthCalled { get; private set; }
        public bool SubmitMfaCalled { get; private set; }
        public bool PollVideoCalled { get; private set; }
        public bool InjectOverlayCalled { get; private set; }
        public bool RevealWindowCalled { get; private set; }
        public bool StopCalled { get; private set; }

        public string? LastSubmittedPin { get; private set; }

        public AuthPageStatus? AuthStatusToReturn { get; set; }
        public VideoPlaybackStatus? VideoStatusToReturn { get; set; }

        public string FindBrowserExecutable() => BrowserPath ?? "/usr/bin/google-chrome-stable";

        public Task<bool> LaunchAsync(string browserPath, string url, int port, string userDataDir, bool visible = false, CancellationToken ct = default)
        {
            LaunchCalled = true;
            LastLaunchVisible = visible;
            IsRunning = LaunchResult;
            return Task.FromResult(LaunchResult);
        }

        public Task<WidevineStatus> CheckWidevineAsync(CancellationToken ct = default) => Task.FromResult(WidevineToReturn);

        public Task<AuthPageStatus?> CheckAuthStatusAsync(CancellationToken ct = default)
        {
            CheckAuthCalled = true;
            return Task.FromResult(AuthStatusToReturn);
        }

        public Task<bool> SubmitMfaPinAsync(string pin, CancellationToken ct = default)
        {
            SubmitMfaCalled = true;
            LastSubmittedPin = pin;
            return Task.FromResult(true);
        }

        public Task<VideoPlaybackStatus?> PollVideoPlaybackAsync(CancellationToken ct = default)
        {
            PollVideoCalled = true;
            return Task.FromResult(VideoSequence != null ? VideoSequence() : VideoStatusToReturn);
        }

        public Task<bool> InjectOverlayScriptAsync(string? script = null, CancellationToken ct = default)
        {
            InjectOverlayCalled = true;
            return Task.FromResult(true);
        }

        public Task<bool> RevealWindowAsync(CancellationToken ct = default)
        {
            RevealWindowCalled = true;
            return Task.FromResult(true);
        }

        public Task StopAsync()
        {
            StopCalled = true;
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    public class NetflixSpikeViewModelTests
    {
        private static VideoPlaybackStatus Video(double t) =>
            new(true, t, 4, false, 300, "https://www.netflix.com/watch/1", "Netflix");

        private static AuthPageStatus Auth(string url, bool login = false, bool mfa = false, bool video = false, double t = 0) =>
            new(url, "t", login, mfa, video, t);

        private static NetflixSpikeViewModel Vm(MockCdpBrowserService svc) => new(svc)
        {
            PollInterval = TimeSpan.FromMilliseconds(5),
            AuthCheckTimeout = TimeSpan.FromMilliseconds(200),
            BufferingTimeout = TimeSpan.FromMilliseconds(300),
        };

        [Fact]
        public async Task Launch_OnLoginPage_EndsNotLoggedIn_WithoutBuffering()
        {
            var svc = new MockCdpBrowserService
            {
                AuthStatusToReturn = Auth("https://www.netflix.com/login", login: true)
            };
            var vm = Vm(svc);

            await vm.LaunchPlaybackFlowAsync();

            Assert.Equal(NetflixSpikeState.NotLoggedIn, vm.State);
            Assert.True(vm.IsProblem);
            Assert.Contains("NOT LOGGED IN", vm.StatusMessage);
            Assert.False(svc.PollVideoCalled);
            Assert.True(svc.StopCalled);
            Assert.False(svc.InjectOverlayCalled);
        }

        [Fact]
        public async Task Launch_WhenBrowserLaunchFails_EndsFailedWithReason()
        {
            var svc = new MockCdpBrowserService { LaunchResult = false, LastError = "no such binary" };
            var vm = Vm(svc);

            await vm.LaunchPlaybackFlowAsync();

            Assert.Equal(NetflixSpikeState.Failed, vm.State);
            Assert.Contains("no such binary", vm.StatusMessage);
            Assert.False(svc.CheckAuthCalled);
        }

        [Fact]
        public async Task Launch_LoggedInButVideoNeverAdvances_FailsAndDoesNotClaimPlaying()
        {
            var svc = new MockCdpBrowserService
            {
                AuthStatusToReturn = Auth("https://www.netflix.com/watch/1", video: true, t: 2.0),
                VideoStatusToReturn = Video(2.0) // frozen
            };
            var vm = Vm(svc);

            await vm.LaunchPlaybackFlowAsync();

            Assert.Equal(NetflixSpikeState.Failed, vm.State);
            Assert.False(svc.RevealWindowCalled);
        }

        [Fact]
        public async Task Launch_LoggedInAndTimeAdvances_ReachesPlaying()
        {
            double t = 0;
            var svc = new MockCdpBrowserService
            {
                AuthStatusToReturn = Auth("https://www.netflix.com/browse"),
                VideoSequence = () => Video(t += 0.4)
            };
            var vm = Vm(svc);

            await vm.LaunchPlaybackFlowAsync();

            Assert.Equal(NetflixSpikeState.Playing, vm.State);
        }

        [Fact]
        public async Task Launch_ReportsWidevineAsSeparateFact()
        {
            var svc = new MockCdpBrowserService
            {
                WidevineToReturn = new WidevineStatus(false, "NotSupportedError"),
                AuthStatusToReturn = Auth("https://www.netflix.com/login", login: true)
            };
            var vm = Vm(svc);

            await vm.LaunchPlaybackFlowAsync();

            Assert.Contains("NOT available", vm.DrmStatus);
            Assert.Contains("NotSupportedError", vm.DrmStatus);
        }

        [Fact]
        public async Task SignIn_LaunchesVisible_AndVerifyOnLoginPageStaysSigningIn()
        {
            var svc = new MockCdpBrowserService
            {
                AuthStatusToReturn = Auth("https://www.netflix.com/login", login: true)
            };
            var vm = Vm(svc);

            await vm.StartSignInAsync();
            Assert.True(svc.LastLaunchVisible);
            Assert.Equal(NetflixSpikeState.SigningIn, vm.State);

            await vm.CompleteSignInAsync();
            Assert.Equal(NetflixSpikeState.SigningIn, vm.State);
            Assert.Contains("Still on a login page", vm.StatusMessage);
        }

        [Fact]
        public async Task SignIn_VerifyOnBrowsePage_ClosesBrowserAndReportsSignedIn()
        {
            var svc = new MockCdpBrowserService
            {
                AuthStatusToReturn = Auth("https://www.netflix.com/login", login: true)
            };
            var vm = Vm(svc);
            await vm.StartSignInAsync();

            svc.AuthStatusToReturn = Auth("https://www.netflix.com/browse");
            await vm.CompleteSignInAsync();

            Assert.Equal(NetflixSpikeState.SignedIn, vm.State);
            Assert.True(svc.StopCalled);
            Assert.True(vm.CanLaunch);
        }

        [Fact]
        public async Task Launch_Hidden_UsesNonVisibleMode()
        {
            var svc = new MockCdpBrowserService { AuthStatusToReturn = Auth("https://www.netflix.com/login", login: true) };
            var vm = Vm(svc);
            await vm.LaunchPlaybackFlowAsync();
            Assert.False(svc.LastLaunchVisible);
        }

        [Fact]
        public void InitialState_ShouldBeIdle_WithDefaultUrl()
        {
            var mockService = new MockCdpBrowserService();
            var vm = new NetflixSpikeViewModel(mockService);

            Assert.Equal(NetflixSpikeState.Idle, vm.State);
            Assert.True(vm.IsIdle);
            Assert.True(vm.CanLaunch);
            Assert.False(vm.CanStop);
            Assert.Equal("https://www.netflix.com/watch/80107105", vm.TargetUrl);
            Assert.Equal("____", vm.MfaPinDisplay);
        }

        [Fact]
        public void SelectPresetCommand_ShouldUpdateTargetUrl()
        {
            var mockService = new MockCdpBrowserService();
            var vm = new NetflixSpikeViewModel(mockService);

            vm.SelectPresetCommand.Execute("https://example.com/test.mp4").Subscribe();

            Assert.Equal("https://example.com/test.mp4", vm.TargetUrl);
        }

        [Fact]
        public void PinManipulation_ShouldWorkCorrectly()
        {
            var mockService = new MockCdpBrowserService();
            var vm = new NetflixSpikeViewModel(mockService);

            vm.AppendMfaPin("1");
            vm.AppendMfaPin("2");
            vm.AppendMfaPin("3");
            Assert.Equal("123", vm.MfaPin);
            Assert.Equal("123", vm.MfaPinDisplay);

            vm.BackspaceMfaPin();
            Assert.Equal("12", vm.MfaPin);

            vm.ClearMfaCommand.Execute().Subscribe();
            Assert.Equal("", vm.MfaPin);
            Assert.Equal("____", vm.MfaPinDisplay);
        }

        [Fact]
        public async Task LaunchFlow_WithSimulatedMfa_ShouldTransitionToMfaRequired()
        {
            var mockService = new MockCdpBrowserService();
            var vm = new NetflixSpikeViewModel(mockService)
            {
                IsSimulateMfaEnabled = true
            };

            await vm.LaunchPlaybackFlowAsync();

            Assert.True(mockService.LaunchCalled);
            Assert.Equal(NetflixSpikeState.MfaRequired, vm.State);
            Assert.True(vm.IsMfaRequired);
            Assert.True(vm.CanStop);
        }

        [Fact]
        public async Task SubmitMfa_ShouldAdvanceToBuffering_AndRevealWhenVideoPlays()
        {
            double t = 1.0;
            var mockService = new MockCdpBrowserService
            {
                VideoSequence = () => Video(t += 0.5)
            };

            var vm = new NetflixSpikeViewModel(mockService)
            {
                IsSimulateMfaEnabled = true,
                PollInterval = TimeSpan.FromMilliseconds(5)
            };

            await vm.LaunchPlaybackFlowAsync();
            Assert.Equal(NetflixSpikeState.MfaRequired, vm.State);

            vm.AppendMfaPin("4");
            vm.AppendMfaPin("8");
            vm.AppendMfaPin("2");
            vm.AppendMfaPin("9");

            await vm.SubmitMfaAsync();

            Assert.Equal(NetflixSpikeState.Playing, vm.State);
            Assert.True(vm.IsPlaying);
            Assert.True(mockService.InjectOverlayCalled);
            Assert.True(mockService.RevealWindowCalled);
        }

        [Fact]
        public async Task StopCommand_ShouldTerminateBrowserAndSetStopped()
        {
            var mockService = new MockCdpBrowserService();
            var vm = new NetflixSpikeViewModel(mockService);

            await vm.StopPlaybackAsync();

            Assert.True(mockService.StopCalled);
            Assert.Equal(NetflixSpikeState.Stopped, vm.State);
            Assert.True(vm.IsStopped);
            Assert.True(vm.CanLaunch);
        }
    }

    public class NetflixPageClassifierTests
    {
        [Theory]
        [InlineData("https://www.netflix.com/login", false, false, false, 0, NetflixPageKind.LoginRequired)]
        [InlineData("https://www.netflix.com/Login?nextpage=%2Fwatch%2F1", false, false, false, 0, NetflixPageKind.LoginRequired)]
        [InlineData("https://www.netflix.com/watch/1?authURL=abc", false, false, false, 0, NetflixPageKind.LoginRequired)]
        [InlineData("https://www.netflix.com/anything", true, false, false, 0, NetflixPageKind.LoginRequired)] // password input
        [InlineData("https://www.netflix.com/browse", false, false, false, 0, NetflixPageKind.LoggedIn)]
        [InlineData("https://www.netflix.com/watch/80107105", false, false, true, 0, NetflixPageKind.LoggedIn)]
        [InlineData("https://www.netflix.com/mfa/verify", false, false, false, 0, NetflixPageKind.MfaRequired)]
        [InlineData("https://www.netflix.com/browse", false, true, false, 0, NetflixPageKind.MfaRequired)] // pin input
        [InlineData("https://www.netflix.com/watch/80107105", false, false, true, 12.5, NetflixPageKind.VideoPlaying)]
        [InlineData("https://www.netflix.com/watch/80107105", false, false, true, 0.05, NetflixPageKind.LoggedIn)] // below threshold
        [InlineData("http://localhost:8000/test.html", false, false, true, 3.0, NetflixPageKind.VideoPlaying)]
        [InlineData("https://www.netflix.com/", false, false, false, 0, NetflixPageKind.Unknown)] // marketing page
        [InlineData("about:blank", false, false, false, 0, NetflixPageKind.Unknown)]
        [InlineData("", false, false, false, 0, NetflixPageKind.Unknown)]
        public void Classify(string url, bool login, bool mfa, bool video, double t, NetflixPageKind expected)
        {
            Assert.Equal(expected, NetflixPageClassifier.Classify(url, login, mfa, video, t));
        }

        [Fact]
        public void LoginWinsOverPlayingVideo_WhenPasswordInputPresent()
        {
            Assert.Equal(NetflixPageKind.LoginRequired,
                NetflixPageClassifier.Classify("https://www.netflix.com/watch/1", true, false, true, 5));
        }
    }
}
