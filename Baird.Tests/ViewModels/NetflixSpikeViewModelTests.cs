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

        public Task<bool> LaunchAsync(string browserPath, string url, int port, string userDataDir, CancellationToken ct = default)
        {
            LaunchCalled = true;
            IsRunning = true;
            return Task.FromResult(true);
        }

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
            return Task.FromResult(VideoStatusToReturn);
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
            var mockService = new MockCdpBrowserService
            {
                // Return video already playing > 0.1s
                VideoStatusToReturn = new VideoPlaybackStatus(
                    Found: true,
                    CurrentTime: 1.5,
                    ReadyState: 4,
                    Paused: false,
                    Duration: 300,
                    Url: "https://www.netflix.com/watch/80107105",
                    Title: "Netflix"
                )
            };

            var vm = new NetflixSpikeViewModel(mockService)
            {
                IsSimulateMfaEnabled = true
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
}
