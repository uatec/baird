using System;

namespace Baird.Models
{
    public class HistoryItem
    {
        public required string Id { get; set; }
        public TimeSpan LastPosition { get; set; }
        public TimeSpan Duration { get; set; }
        public bool IsFinished { get; set; }
        public DateTime LastWatched { get; set; }

        public double Progress => Duration.TotalSeconds > 0 ? LastPosition.TotalSeconds / Duration.TotalSeconds : 0;
    }
}
