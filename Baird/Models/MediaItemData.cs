using System;
using Baird.Services;

namespace Baird.Models
{
    /// <summary>
    /// Pure data transfer object for media items.
    /// Contains only serializable data, no UI state or computed properties.
    /// </summary>
    public record MediaItemData
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Details { get; init; }
        public required string ImageUrl { get; init; }
        public required bool IsLive { get; init; }
        public string? StreamUrl { get; init; }
        public required string Source { get; init; }
        public string? ChannelNumber { get; init; }
        public required MediaType Type { get; init; }
        public required string Synopsis { get; init; }
        public required string Subtitle { get; init; }
        public TimeSpan Duration { get; init; }

        /// <summary>For an episode: the show it belongs to (its id in <see cref="Source"/>, as the provider lists it, and its name). Null for anything else.</summary>
        public string? SeriesId { get; init; }
        public string? SeriesName { get; init; }

        public bool HasDuration => Duration > TimeSpan.Zero;
    }
}
