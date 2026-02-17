using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    public interface IJellyseerrService
    {
        Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string query, int page, CancellationToken cancellationToken = default);
        Task<JellyseerrRequestResponse> CreateRequestAsync(int mediaId, string mediaType, CancellationToken cancellationToken = default);
        Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken cancellationToken = default);
    }

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
        public bool IsAvailable => MediaInfoStatus == 5 || MediaInfoStatus == 4;
    }

    public class JellyseerrRequestResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int? RequestId { get; set; }
    }
}
