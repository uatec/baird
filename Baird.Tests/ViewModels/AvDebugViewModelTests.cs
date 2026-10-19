using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baird.Mpv;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class AvDebugViewModelTests
    {
        private class FakeSource : IAvDiagnosticsSource
        {
            public AvStats? Next { get; set; }
            public List<double> AudioDelays { get; } = new();

            public Task<AvStats?> SampleAvStatsAsync() => Task.FromResult(Next);
            public void SetAudioDelay(double seconds) => AudioDelays.Add(seconds);
        }

        private static AvStats Playing(string path, long decoderDrops = 0, double avsync = 0.0) => new()
        {
            State = PlaybackState.Playing,
            Path = path,
            AvSyncSeconds = avsync,
            DecoderDrops = decoderDrops,
            VoDrops = 0,
            DelayedFrames = 0,
            MistimedFrames = 0,
            VideoSyncMode = "display-vdrop",
            DisplaySyncActive = true,
        };

        private static string Row(AvDebugViewModel vm, string name) =>
            vm.Groups.SelectMany(g => g.Rows).Single(r => r.Name == name).Value;

        private static bool RowWarns(AvDebugViewModel vm, string name) =>
            vm.Groups.SelectMany(g => g.Rows).Single(r => r.Name == name).IsWarning;

        [Fact]
        public async Task Sample_NewFile_AddsPlayingEventWithTitle()
        {
            var source = new FakeSource { Next = Playing("http://tvh/stream/channel/abc") };
            var vm = new AvDebugViewModel { Source = source, CurrentTitle = () => "BBC One HD" };

            await vm.SampleAsync();

            Assert.Contains(vm.Events, e => e.Message == "Playing: BBC One HD (abc)");
            Assert.Equal("BBC One HD (abc)", Row(vm, "File"));
        }

        [Fact]
        public async Task Sample_DecoderDropsIncrease_RaisesWarningEvent()
        {
            var source = new FakeSource { Next = Playing("a.mkv") };
            var vm = new AvDebugViewModel { Source = source };
            await vm.SampleAsync();

            source.Next = Playing("a.mkv", decoderDrops: 3);
            await vm.SampleAsync();

            Assert.Contains(vm.Events, e => e.Message == "+3 decoder drops" && e.IsWarning);
            Assert.Equal("3", Row(vm, "Decoder drops"));
            Assert.True(RowWarns(vm, "Decoder drops"));
        }

        [Fact]
        public async Task Sample_AvSyncBeyondThreshold_WarnsOnceUntilBackInRange()
        {
            var source = new FakeSource { Next = Playing("a.mkv", avsync: 0.065) };
            var vm = new AvDebugViewModel { Source = source };

            await vm.SampleAsync();
            await vm.SampleAsync();
            source.Next = Playing("a.mkv", avsync: 0.005);
            await vm.SampleAsync();

            Assert.Single(vm.Events, e => e.Message.StartsWith("A/V offset +65.0 ms", StringComparison.Ordinal));
            Assert.Contains(vm.Events, e => e.Message.StartsWith("A/V offset back in range", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ResetCounters_ShowsDropsRelativeToReset()
        {
            var source = new FakeSource { Next = Playing("a.mkv", decoderDrops: 5) };
            var vm = new AvDebugViewModel { Source = source };
            await vm.SampleAsync();

            vm.ResetCountersCommand.Execute().Subscribe();
            source.Next = Playing("a.mkv", decoderDrops: 7);
            await vm.SampleAsync();

            Assert.Equal("2", Row(vm, "Decoder drops"));
        }

        [Fact]
        public void AudioDelayCommands_StepInTensOfMillisecondsAndReset()
        {
            var source = new FakeSource();
            var vm = new AvDebugViewModel { Source = source };

            vm.AudioDelayLaterCommand.Execute().Subscribe();
            vm.AudioDelayLaterCommand.Execute().Subscribe();
            vm.AudioDelayEarlierCommand.Execute().Subscribe();
            Assert.Equal(10, vm.AudioDelayMs);
            Assert.Equal("Audio delay: +10 ms (audio later)", vm.AudioDelayText);

            vm.AudioDelayResetCommand.Execute().Subscribe();

            Assert.Equal(new[] { 0.01, 0.02, 0.01, 0.0 }, source.AudioDelays.Select(d => Math.Round(d, 3)));
        }

        [Fact]
        public void Mark_AddsNumberedWarningEvent()
        {
            var vm = new AvDebugViewModel();

            vm.Mark();
            vm.Mark();

            Assert.Equal("MARK #2 — user flagged an issue", vm.Events[0].Message);
            Assert.True(vm.Events[0].IsWarning);
        }

        [Fact]
        public async Task Sample_WithoutPlayer_StillShowsSystemStats()
        {
            var vm = new AvDebugViewModel();

            await vm.SampleAsync();

            Assert.Equal("–", Row(vm, "A/V offset"));
            Assert.Empty(vm.Events);
        }
    }
}
