using System;

namespace Baird.Playback
{
    /// <summary>
    /// An engine whose service puts questions on the video: "Skip intro", "Skip recap", "Next episode". Baird draws them itself
    /// (the service's own page is hidden behind Baird's window) and the OK key answers them. Optional: mpv has none.
    /// </summary>
    public interface IPromptingEngine : IPlaybackEngine
    {
        /// <summary>The prompt on screen now ("Skip intro"), or null when there is none.</summary>
        string? PromptText { get; }

        /// <summary>The prompt appeared, changed or went away. Can fire on any thread.</summary>
        event EventHandler? PromptChanged;

        /// <summary>Activates the prompt on screen (what the viewer chose with OK). False when there is none.</summary>
        bool ActivatePrompt();
    }
}
