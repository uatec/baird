
using System;

namespace Baird.ViewModels
{
    public class ActiveMedia
    {
        public string Name { get; set; }
        public string StreamUrl { get; set; }
        public string Details { get; set; }
        public string ImageUrl { get; set; }
        public string Source { get; set; }
        public Baird.Services.MediaType Type { get; set; }
        public string Synopsis { get; set; }
        public string Subtitle { get; set; }
        public bool IsLive { get; set; }
        public string? ChannelNumber { get; set; }
        public string Id { get; set; }
        public TimeSpan? ResumeTime { get; set; }
    }
}
