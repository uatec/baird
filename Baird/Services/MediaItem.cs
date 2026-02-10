using System;

namespace Baird.Services
{
    public enum MediaType
    {
        Video,
        Audio,
        Brand,
        Folder,
        Channel,
    }

    public class MediaItem
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public required string Details { get; set; } // e.g. Year, Channel Number
        public required string ImageUrl { get; set; }
        public required bool IsLive { get; set; }
        public string? StreamUrl { get; set; } // Can be empty if Type is Brand
        public required string Source { get; set; } // e.g. "Live TV", "Jellyfin: home", "YouTube"
        public string? ChannelNumber { get; set; }

        public required MediaType Type { get; set; }
        public required string Synopsis { get; set; } // keep this?
        public required string Subtitle { get; set; } // e.g. "Series 1: Episode 1"

        // History Tracking
        public TimeSpan LastPosition { get; set; }
        public TimeSpan Duration { get; set; }
        public bool IsFinished { get; set; }
        public DateTime LastWatched { get; set; }

        public double Progress => LastPosition.TotalSeconds / Duration.TotalSeconds;
    }
}
