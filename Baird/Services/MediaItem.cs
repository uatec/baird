using System;

namespace Baird.Services
{
    public enum MediaType
    {
        Video,
        Audio,
        Brand,
        Folder
    }

    public class MediaItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Details { get; set; } // e.g. Year, Channel Number
        public string ImageUrl { get; set; }
        public bool IsLive { get; set; }
        public string StreamUrl { get; set; } // Can be empty if Type is Brand
        public string Source { get; set; } // e.g. "Live TV", "Jellyfin: home", "YouTube"
        public string? ChannelNumber { get; set; }
        
        public MediaType Type { get; set; } = MediaType.Video;
        public string Synopsis { get; set; } = "";
        public string Subtitle { get; set; } = ""; // e.g. "Series 1: Episode 1"

        // History Tracking
        public TimeSpan LastPosition { get; set; }
        public TimeSpan Duration { get; set; }
        public bool IsFinished { get; set; }
        public DateTime LastWatched { get; set; }

        public double Progress => LastPosition.TotalSeconds / Duration.TotalSeconds;
    }
}
