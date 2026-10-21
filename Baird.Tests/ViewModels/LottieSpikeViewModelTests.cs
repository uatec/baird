using System;
using System.Reactive.Linq;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class LottieSpikeViewModelTests
    {
        [Fact]
        public void DefaultState_InitializesCorrectly()
        {
            var vm = new LottieSpikeViewModel();

            Assert.NotNull(vm.Animations);
            Assert.Equal(4, vm.Animations.Count);
            Assert.NotNull(vm.SelectedAnimation);
            Assert.Equal("Loading Ring", vm.SelectedAnimation.Title);
            Assert.Equal("avares://Baird/Assets/Lottie/loading-ring.json", vm.CurrentAnimationPath);
            Assert.True(vm.IsPlaying);
            Assert.Equal(1, vm.PlaybackRate);
            Assert.Equal("1x", vm.PlaybackRateText);
            Assert.True(vm.IsLooping);
            Assert.Equal(-1, vm.RepeatCount);
            Assert.Equal(0, vm.RepetitionCount);
            Assert.Contains("Playing (1x)", vm.StatusText);
        }

        [Fact]
        public void TogglePlayPauseCommand_TogglesPlaybackState()
        {
            var vm = new LottieSpikeViewModel();
            bool pauseRequested = false;
            bool playRequested = false;
            vm.RequestPause += () => pauseRequested = true;
            vm.RequestPlay += () => playRequested = true;

            Assert.True(vm.IsPlaying);
            Assert.Equal("Pause ⏸", vm.PlayPauseButtonText);

            vm.TogglePlayPauseCommand.Execute().Subscribe();

            Assert.False(vm.IsPlaying);
            Assert.True(pauseRequested);
            Assert.Equal("Play ▶", vm.PlayPauseButtonText);
            Assert.Contains("Paused", vm.StatusText);

            vm.TogglePlayPauseCommand.Execute().Subscribe();

            Assert.True(vm.IsPlaying);
            Assert.True(playRequested);
            Assert.Equal("Pause ⏸", vm.PlayPauseButtonText);
            Assert.Contains("Playing", vm.StatusText);
        }

        [Fact]
        public void SetSpeedCommand_UpdatesPlaybackRate()
        {
            var vm = new LottieSpikeViewModel();

            vm.SetSpeedCommand.Execute(2).Subscribe();
            Assert.Equal(2, vm.PlaybackRate);
            Assert.Equal("2x", vm.PlaybackRateText);

            vm.SetSpeedCommand.Execute(3).Subscribe();
            Assert.Equal(3, vm.PlaybackRate);
            Assert.Equal("3x", vm.PlaybackRateText);
        }

        [Fact]
        public void ToggleLoopCommand_TogglesLoopMode()
        {
            var vm = new LottieSpikeViewModel();
            Assert.True(vm.IsLooping);
            Assert.Equal(-1, vm.RepeatCount);

            vm.ToggleLoopCommand.Execute().Subscribe();
            Assert.False(vm.IsLooping);
            Assert.Equal(1, vm.RepeatCount);

            vm.ToggleLoopCommand.Execute().Subscribe();
            Assert.True(vm.IsLooping);
            Assert.Equal(-1, vm.RepeatCount);
        }

        [Fact]
        public void NextAndPreviousAnimationCommands_CycleProperly()
        {
            var vm = new LottieSpikeViewModel();
            Assert.Equal("Loading Ring", vm.SelectedAnimation.Title);

            vm.NextAnimationCommand.Execute().Subscribe();
            Assert.Equal("Audio Visualizer Bars", vm.SelectedAnimation.Title);

            vm.NextAnimationCommand.Execute().Subscribe();
            Assert.Equal("Play / Pause Toggle", vm.SelectedAnimation.Title);

            vm.PreviousAnimationCommand.Execute().Subscribe();
            Assert.Equal("Audio Visualizer Bars", vm.SelectedAnimation.Title);
        }

        [Fact]
        public void SelectAnimationCommand_ChangesSelectedAnimationAndResetsLoops()
        {
            var vm = new LottieSpikeViewModel();
            vm.RepetitionCount = 5;

            var target = vm.Animations[3]; // Wave Pulse Loader
            vm.SelectAnimationCommand.Execute(target).Subscribe();

            Assert.Equal(target, vm.SelectedAnimation);
            Assert.Equal("avares://Baird/Assets/Lottie/wave-loader.json", vm.CurrentAnimationPath);
            Assert.Equal(0, vm.RepetitionCount);
        }

        [Fact]
        public void SeekCommand_RaisesRequestSeekEvent()
        {
            var vm = new LottieSpikeViewModel();
            float receivedProgress = -1f;
            vm.RequestSeek += p => receivedProgress = p;

            vm.SeekCommand.Execute(0.75).Subscribe();

            Assert.Equal(0.75, vm.SeekProgress);
            Assert.Equal(0.75f, receivedProgress);
            Assert.Equal("75%", vm.ProgressPercentText);
        }

        [Fact]
        public void OnRepetitionCompleted_UpdatesRepetitionCountAndStatus()
        {
            var vm = new LottieSpikeViewModel();
            Assert.Equal(0, vm.RepetitionCount);

            vm.OnRepetitionCompleted(3);

            Assert.Equal(3, vm.RepetitionCount);
            Assert.Contains("Loops: 3", vm.StatusText);
        }

        [Fact]
        public void BackCommand_FiresBackRequestedEvent()
        {
            var vm = new LottieSpikeViewModel();
            bool fired = false;
            vm.BackRequested += (s, e) => fired = true;

            vm.BackCommand.Execute().Subscribe();

            Assert.True(fired);
        }
    }
}
