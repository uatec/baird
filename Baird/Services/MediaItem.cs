using System;

namespace Baird.Services
{
    public class MediaItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Details { get; set; } // e.g. Year, Channel Number
        public string ImageUrl { get; set; }
        public bool IsLive { get; set; }
    }
}
