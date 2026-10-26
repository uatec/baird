using System;
using System.Threading.Tasks;
using Baird.Services;

namespace Baird.NewUi.Core
{
    /// <summary>
    /// The remote's voice key: hold to talk, let go to search for what was said (the classic speech service, Whisper).
    /// Key repeats while held don't start a second recording.
    /// </summary>
    public sealed class VoiceSearch
    {
        private readonly ISpeechToTextService _speech;
        private readonly Action<string> _search;
        private readonly Action<string> _status;
        private bool _listening;

        public VoiceSearch(ISpeechToTextService speech, Action<string> search, Action<string> status)
        {
            _speech = speech; _search = search; _status = status;
        }

        public bool Listening => _listening;

        public async Task PressAsync()
        {
            if (_listening) return;
            _listening = true;
            _status("Listening…");
            try { await _speech.StartRecordingAsync(); }
            catch (Exception ex) { _listening = false; _status($"The microphone isn't working: {ex.Message}"); }
        }

        public async Task ReleaseAsync()
        {
            if (!_listening) return;
            _listening = false;
            string? heard;
            try { heard = await _speech.StopAndTranscribeAsync(); }
            catch (Exception ex) { _status($"Voice search failed: {ex.Message}"); return; }
            if (string.IsNullOrWhiteSpace(heard)) { _status("Sorry, I didn't catch that. Hold the voice button and try again."); return; }
            _search(heard.Trim().TrimEnd('.', '!', '?'));
        }
    }
}
