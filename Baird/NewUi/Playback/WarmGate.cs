using System;
using Baird.NewUi.Adapters;

namespace Baird.NewUi.Playback
{
    /// <summary>
    /// Tells the work queue how hard it may work, so background fetching never costs the viewer a frame.
    /// Idle: Run. Video playing: Slow. Video playing and the player's cumulative dropped-frame count went up within
    /// <see cref="DropWindow"/>: Paused. Video playing full-screen with Home closed: Paused (nothing on screen needs data).
    /// Inputs are plain delegates reading values the shell samples, so this never touches the player itself.
    /// </summary>
    public sealed class WarmGate : IWarmGate
    {
        public static readonly TimeSpan DropWindow = TimeSpan.FromSeconds(10);

        private readonly Func<bool> _isPlaying;
        private readonly Func<long> _droppedFrames;
        private readonly Func<bool> _fullScreenPlayback;
        private readonly Func<DateTimeOffset> _now;
        private readonly object _lock = new();
        private long? _lastCount;
        private DateTimeOffset? _lastIncrease;

        /// <param name="fullScreenPlayback">True when the video fills the screen and Home is not showing (the viewer is only watching).</param>
        public WarmGate(Func<bool> isPlaying, Func<long> droppedFrames, Func<bool> fullScreenPlayback, Func<DateTimeOffset> now)
        {
            _isPlaying = isPlaying;
            _droppedFrames = droppedFrames;
            _fullScreenPlayback = fullScreenPlayback;
            _now = now;
        }

        /// <summary>True when only the video is on screen: playing, at the video, with no overlay and no page stacked above it (Home open means not at the video).</summary>
        public static bool IsFullScreenPlayback(bool playing, bool atVideo, bool overlay, int pages) => playing && atVideo && !overlay && pages == 0;

        /// <summary>
        /// The gate's "playing" input: something is playing or loading, and not paused. A stream that is loading (a channel change) counts:
        /// it is when the player is busiest, so warming goes Slow rather than Run. A paused video does not (nothing to disturb).
        /// Only the gate reads it this way; elsewhere loading is not playing.
        /// </summary>
        public static bool BusyPlaying(bool current, bool paused, bool loading) => current && (loading || !paused);

        public WarmMode Mode
        {
            get
            {
                try
                {
                    lock (_lock)
                    {
                        if (!_isPlaying()) { _lastCount = null; _lastIncrease = null; return WarmMode.Run; }
                        var now = _now();
                        var count = _droppedFrames();
                        // A count going down is a new video starting, not a drop.
                        if (_lastCount is { } last && count > last) _lastIncrease = now;
                        _lastCount = count;
                        if (_fullScreenPlayback()) return WarmMode.Paused;
                        if (_lastIncrease is { } at && now - at < DropWindow) return WarmMode.Paused;
                        return WarmMode.Slow;
                    }
                }
                catch (Exception) { return WarmMode.Slow; }
            }
        }
    }
}
