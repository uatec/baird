using System;

namespace Baird.Playback
{
    /// <summary>
    /// An engine that plays a streaming site's video in the shared browser behind Baird's (transparent) window: Netflix,
    /// Disney+. <see cref="Baird.Controls.VideoPlayer"/> picks the engine whose <see cref="CanPlay"/> accepts the URL, shows
    /// <see cref="StatusText"/> while it loads and the <see cref="Failed"/> reason if it cannot play.
    /// </summary>
    public interface IBrowserPlaybackEngine : IPlaybackEngine
    {
        /// <summary>The service's name for the viewer: "Netflix", "Disney+".</summary>
        string Name { get; }

        /// <summary>True for a URL this engine plays (the item's stream URL).</summary>
        bool CanPlay(string? url);

        /// <summary>A simple text for the current step while the video loads.</summary>
        string StatusText { get; }

        event EventHandler? StatusChanged;

        /// <summary>The service could not play (the reason, for the viewer). Raised once per play.</summary>
        event EventHandler<string>? Failed;
    }
}
