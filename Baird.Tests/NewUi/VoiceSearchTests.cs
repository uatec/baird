using System.Collections.Generic;
using System.Threading.Tasks;
using Baird.NewUi.Core;
using Baird.Services;
using Xunit;

namespace Baird.Tests.NewUi
{
    public class VoiceSearchTests
    {
        private sealed class Speech : ISpeechToTextService
        {
            public readonly List<string> Calls = new();
            public string? Heard = "loki";
            public Task StartRecordingAsync() { Calls.Add("start"); return Task.CompletedTask; }
            public Task<string?> StopAndTranscribeAsync() { Calls.Add("stop"); return Task.FromResult(Heard); }
        }

        [Fact]
        public async Task Holding_the_voice_key_records_and_letting_go_searches_for_what_was_said()
        {
            var speech = new Speech(); var said = new List<string>(); var status = new List<string>();
            var voice = new VoiceSearch(speech, said.Add, status.Add);
            await voice.PressAsync();
            await voice.PressAsync(); // key repeat while held: still one recording
            await voice.ReleaseAsync();
            Assert.Equal(new[] { "start", "stop" }, speech.Calls);
            Assert.Equal(new[] { "loki" }, said);
            Assert.Contains("Listening…", status);
        }

        [Fact]
        public async Task Nothing_heard_says_so_and_searches_nothing()
        {
            var speech = new Speech { Heard = "  " }; var said = new List<string>(); var status = new List<string>();
            var voice = new VoiceSearch(speech, said.Add, status.Add);
            await voice.PressAsync();
            await voice.ReleaseAsync();
            Assert.Empty(said);
            Assert.Contains(status, s => s.Contains("didn't catch"));
        }
    }
}
