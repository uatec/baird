using System.Threading.Tasks;

namespace Baird.Mpv
{
    /// <summary>
    /// Render-loop timing measured on our side of the mpv render API over one sample window.
    /// "Latency" is the time from mpv's update callback (new frame ready) to us actually calling
    /// mpv_render_context_render — i.e. how long the UI/render thread kept the frame waiting.
    /// </summary>
    public readonly record struct RenderTiming(
        int Renders,
        int UpdateCallbacks,
        double IntervalAvgMs,
        double IntervalMaxMs,
        double RenderAvgMs,
        double RenderMaxMs,
        double LatencyAvgMs,
        double LatencyMaxMs,
        int LateRenders);

    /// <summary>Snapshot of mpv's A/V timing state. Null fields = property unavailable.</summary>
    public sealed record AvStats
    {
        public PlaybackState State { get; init; }
        public string? Path { get; init; }

        // Sync
        public double? AvSyncSeconds { get; init; }
        public double? TotalAvSyncChangeSeconds { get; init; }
        public double? AudioDelaySeconds { get; init; }
        public bool? DisplaySyncActive { get; init; }
        public string? VideoSyncMode { get; init; }

        // Frames (cumulative per file, as reported by mpv)
        public long? DecoderDrops { get; init; }
        public long? VoDrops { get; init; }
        public long? MistimedFrames { get; init; }
        public long? DelayedFrames { get; init; }

        // Display
        public double? EstimatedDisplayFps { get; init; }
        public double? VsyncJitter { get; init; }
        public double? EstimatedVideoFps { get; init; }
        public double? ContainerFps { get; init; }

        // Source
        public string? VideoCodec { get; init; }
        public string? HwdecCurrent { get; init; }
        public string? PixelFormat { get; init; }
        public long? Width { get; init; }
        public long? Height { get; init; }
        public string? VideoFilters { get; init; }
        public bool? DeinterlaceActive { get; init; }
        public string? AudioCodec { get; init; }
        public long? AudioSampleRate { get; init; }
        public string? AudioOutput { get; init; }

        // Buffer
        public double? CacheSeconds { get; init; }
        public bool? PausedForCache { get; init; }

        public RenderTiming Render { get; init; }
    }

    /// <summary>Read-only diagnostics plus the audio-delay knob, for the A/V debug page.</summary>
    public interface IAvDiagnosticsSource
    {
        /// <summary>Samples stats off the render thread. Returns null if no player is available.</summary>
        Task<AvStats?> SampleAvStatsAsync();

        /// <summary>Shifts audio relative to video. Positive = audio later.</summary>
        void SetAudioDelay(double seconds);
    }
}
