using System;
using System.Collections.Generic;
using Baird.ViewModels;

namespace Baird.Models
{
    public class EpgProgram
    {
        public string Id { get; set; } = string.Empty;
        public string ChannelId { get; set; } = string.Empty;
        public string ChannelName { get; set; } = string.Empty;
        public string? ChannelNumber { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public string? Summary { get; set; }
        public string? Description { get; set; }
        public long StartUnix { get; set; }
        public long StopUnix { get; set; }
        /// <summary>A poster or still for the programme, when the guide source has one.</summary>
        public string? ImageUrl { get; set; }

        public bool IsStreamingEpisode { get; set; }
        public double? StreamingProgress { get; set; }
        public MediaItemViewModel? MediaItem { get; set; }

        public DateTimeOffset StartTime => StartUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(StartUnix) : DateTimeOffset.MinValue;
        public DateTimeOffset StopTime => StopUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(StopUnix) : DateTimeOffset.MinValue;

        private TimeSpan? _customDuration;
        public TimeSpan Duration
        {
            get
            {
                if (_customDuration.HasValue && _customDuration.Value > TimeSpan.Zero)
                    return _customDuration.Value;
                if (StopTime > StartTime && StartTime > DateTimeOffset.MinValue)
                    return StopTime - StartTime;
                return TimeSpan.Zero;
            }
            set => _customDuration = value;
        }

        public bool IsCurrent
        {
            get
            {
                if (IsStreamingEpisode) return false;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return StartUnix <= now && now < StopUnix;
            }
        }

        public double Progress
        {
            get
            {
                if (IsStreamingEpisode)
                {
                    return StreamingProgress ?? 0.0;
                }
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (now < StartUnix) return 0.0;
                if (now >= StopUnix) return 1.0;
                var total = StopUnix - StartUnix;
                return total > 0 ? Math.Clamp((double)(now - StartUnix) / total, 0.0, 1.0) : 0.0;
            }
        }

        public string FormattedTime => IsStreamingEpisode
            ? (Duration > TimeSpan.Zero ? $"{(int)Duration.TotalMinutes} mins" : "On Demand")
            : (StartTime > DateTimeOffset.MinValue ? $"{StartTime.ToLocalTime():HH:mm} - {StopTime.ToLocalTime():HH:mm}" : "On Demand");

        public string FormattedDuration => Duration > TimeSpan.Zero ? $"{(int)Duration.TotalMinutes}m" : "";

        public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title : "No Information";
        public string DisplayDetails => !string.IsNullOrWhiteSpace(Description)
            ? Description
            : (!string.IsNullOrWhiteSpace(Summary) ? Summary : (!string.IsNullOrWhiteSpace(Subtitle) ? Subtitle : string.Empty));
    }

    public class ChannelSchedule
    {
        public MediaItemViewModel Channel { get; set; } = null!;
        public EpgProgram? Now { get; set; }
        public EpgProgram? Next { get; set; }
        public List<EpgProgram> Upcoming { get; set; } = new();
    }
}
