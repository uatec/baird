using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    public interface IJellyseerrService
    {
        Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string query, int page, CancellationToken cancellationToken = default);
        Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int page, CancellationToken cancellationToken = default);
        Task<JellyseerrRequestResponse> CreateRequestAsync(int mediaId, string mediaType, CancellationToken cancellationToken = default);
        Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Seerr's view of one title (GET /api/v1/movie|tv/{tmdbId}): its seasons, what's requested or in the library, and
        /// download progress. Null when Seerr can't say.
        /// </summary>
        Task<JellyseerrMediaStatus?> GetMediaStatusAsync(int tmdbId, string mediaType, CancellationToken cancellationToken = default) =>
            Task.FromResult<JellyseerrMediaStatus?>(null);

        /// <summary>
        /// Request some seasons of a show (Seerr requests whole seasons; it can't request a single episode).
        /// A film, or null seasons, requests the whole title.
        /// </summary>
        Task<JellyseerrRequestResponse> RequestSeasonsAsync(int mediaId, string mediaType, IReadOnlyList<int>? seasons, CancellationToken cancellationToken = default) =>
            CreateRequestAsync(mediaId, mediaType, cancellationToken);
    }

    /// <summary>Seerr's state of one title. Status: 1 unknown, 2 pending, 3 processing, 4 partly available, 5 available.</summary>
    public sealed record JellyseerrMediaStatus(int Status, IReadOnlyList<JellyseerrSeason> Seasons, IReadOnlyList<JellyseerrDownload> Downloads)
    {
        /// <summary>Overall download progress (0–1) of what's downloading, optionally only one season or one episode.</summary>
        public double? Progress(int? season = null, int? episode = null)
        {
            var items = Downloads.Where(d => (season == null || d.Season == season) && (episode == null || d.Episode == episode)).ToList();
            var size = items.Sum(d => d.Size);
            return items.Count == 0 || size <= 0 ? null : (double)(size - items.Sum(d => d.SizeLeft)) / size;
        }
    }

    /// <summary>A season as TMDB lists it, with Seerr's status for it (1 when nothing is known).</summary>
    public sealed record JellyseerrSeason(int Number, int EpisodeCount, int Status);

    /// <summary>One download in progress (Radarr/Sonarr via Seerr); Season/Episode are set for episodes.</summary>
    public sealed record JellyseerrDownload(int? Season, int? Episode, long Size, long SizeLeft);

    public class JellyseerrSearchResult
    {
        public int Id { get; set; }
        public string MediaType { get; set; } = string.Empty; // "movie" or "tv"
        public string Title { get; set; } = string.Empty;
        public string? PosterPath { get; set; }
        public string? BackdropPath { get; set; }
        public string? Overview { get; set; }
        public string? ReleaseDate { get; set; }
        public double VoteAverage { get; set; }
        public int MediaInfoStatus { get; set; } // 1=UNKNOWN, 2=PENDING, 3=PROCESSING, 4=PARTIALLY_AVAILABLE, 5=AVAILABLE
        public bool IsAvailable => MediaInfoStatus == 5;
    }

    public class JellyseerrRequest
    {
        public int Id { get; set; }
        public int Status { get; set; } // 1=PENDING, 2=APPROVED, 3=DECLINED
        public string Title { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty; // "movie" or "tv"
        public string? PosterPath { get; set; }
        public int TmdbId { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public int MediaInfoStatus { get; set; } // Same as search result
        public double? DownloadProgress { get; set; } // null = no data, 0-100 = real % from Radarr/Sonarr
        public bool IsAvailable => MediaInfoStatus == 5 || MediaInfoStatus == 4;
    }

    public class JellyseerrRequestResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int? RequestId { get; set; }
    }
}
