using System;
using Baird.Mpv;

namespace Baird.Playback
{
    /// <summary>
    /// What <see cref="Baird.Controls.VideoPlayer"/> plays through: mpv for most sources, the Netflix browser for Netflix.
    /// Everything above the player (HUD, keys, history) is the same for every engine. Events can fire on any thread.
    /// </summary>
    public interface IPlaybackEngine
    {
        PlaybackState State { get; }
        double TimePositionSeconds { get; }
        double DurationSeconds { get; }

        /// <summary>The video is playing.</summary>
        event EventHandler? FileLoaded;
        event EventHandler? StreamEnded;
        event EventHandler<bool>? PauseStateChanged;
        event EventHandler<double>? DurationChanged;

        void Play(string url, double? startSeconds = null);
        void Pause();
        void Resume();
        void Seek(double seconds);
        void SeekRelative(double seconds);
        void SetSubtitle(bool enabled);
        void Stop();
    }
}
