using System.Threading.Tasks;

namespace Baird.Services
{
    public interface ISpeechToTextService
    {
        /// <summary>
        /// Starts recording audio from the default microphone.
        /// </summary>
        Task StartRecordingAsync();

        /// <summary>
        /// Stops recording and transcribes the captured audio.
        /// Returns the transcript, or null if recording/transcription failed.
        /// </summary>
        Task<string?> StopAndTranscribeAsync();
    }
}
