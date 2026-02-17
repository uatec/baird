using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Baird.Services
{
    public class JellyseerrService : IJellyseerrService
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string _apiKey;

        public JellyseerrService(IConfiguration config)
        {
            _baseUrl = config["JELLYSEERR_URL"]?.TrimEnd('/') ?? "http://localhost:5055";
            _apiKey = config["JELLYSEERR_API_KEY"] ?? "";

            _httpClient = new HttpClient();
            _httpClient.BaseAddress = new Uri(_baseUrl + "/");

            if (!string.IsNullOrEmpty(_apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
            }

            Console.WriteLine($"[JellyseerrService] Configured for {_baseUrl}");
        }

        public async Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string query, int page, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Array.Empty<JellyseerrSearchResult>();
            }

            try
            {
                var url = $"api/v1/search?query={Uri.EscapeDataString(query)}&page={page}";
                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[JellyseerrService] Search failed: {response.StatusCode}");
                    return Array.Empty<JellyseerrSearchResult>();
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                var results = new List<JellyseerrSearchResult>();

                if (doc.RootElement.TryGetProperty("results", out var resultsArray))
                {
                    foreach (var item in resultsArray.EnumerateArray())
                    {
                        var mediaType = item.GetProperty("mediaType").GetString() ?? "";

                        // Skip person results
                        if (mediaType == "person") continue;

                        var result = new JellyseerrSearchResult
                        {
                            Id = item.GetProperty("id").GetInt32(),
                            MediaType = mediaType
                        };

                        // Movies have "title", TV shows have "name"
                        if (mediaType == "movie")
                        {
                            result.Title = item.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "";
                            result.ReleaseDate = item.TryGetProperty("releaseDate", out var releaseDate) ? releaseDate.GetString() : null;
                        }
                        else if (mediaType == "tv")
                        {
                            result.Title = item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "";
                            result.ReleaseDate = item.TryGetProperty("firstAirDate", out var firstAirDate) ? firstAirDate.GetString() : null;
                        }

                        result.PosterPath = item.TryGetProperty("posterPath", out var posterPath) ? posterPath.GetString() : null;
                        result.BackdropPath = item.TryGetProperty("backdropPath", out var backdropPath) ? backdropPath.GetString() : null;
                        result.Overview = item.TryGetProperty("overview", out var overview) ? overview.GetString() : null;
                        result.VoteAverage = item.TryGetProperty("voteAverage", out var voteAverage) ? voteAverage.GetDouble() : 0;

                        // Get media info status
                        if (item.TryGetProperty("mediaInfo", out var mediaInfo))
                        {
                            result.MediaInfoStatus = mediaInfo.TryGetProperty("status", out var status) ? status.GetInt32() : 1;
                        }

                        results.Add(result);
                    }
                }

                Console.WriteLine($"[JellyseerrService] Search '{query}' returned {results.Count} results");
                return results;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JellyseerrService] Search error: {ex.Message}");
                return Array.Empty<JellyseerrSearchResult>();
            }
        }

        public async Task<JellyseerrRequestResponse> CreateRequestAsync(int mediaId, string mediaType, CancellationToken cancellationToken = default)
        {
            try
            {
                var requestBody = new Dictionary<string, object>
                {
                    ["mediaType"] = mediaType,
                    ["mediaId"] = mediaId
                };

                // For TV shows, request all seasons
                if (mediaType == "tv")
                {
                    requestBody["seasons"] = "all";
                }

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("api/v1/request", content, cancellationToken);
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(responseJson);
                    var requestId = doc.RootElement.TryGetProperty("id", out var id) ? id.GetInt32() : 0;

                    Console.WriteLine($"[JellyseerrService] Successfully created request {requestId} for {mediaType} {mediaId}");

                    return new JellyseerrRequestResponse
                    {
                        Success = true,
                        Message = $"Successfully requested {mediaType}",
                        RequestId = requestId
                    };
                }
                else
                {
                    Console.WriteLine($"[JellyseerrService] Request failed: {response.StatusCode} - {responseJson}");
                    return new JellyseerrRequestResponse
                    {
                        Success = false,
                        Message = $"Failed to create request: {response.StatusCode}"
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JellyseerrService] Create request error: {ex.Message}");
                return new JellyseerrRequestResponse
                {
                    Success = false,
                    Message = $"Error: {ex.Message}"
                };
            }
        }

        public async Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Fetch recent requests sorted by modified date
                var url = "api/v1/request?sort=modified&sortDirection=desc&take=100";
                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[JellyseerrService] Get requests failed: {response.StatusCode}");
                    return Array.Empty<JellyseerrRequest>();
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                var requests = new List<JellyseerrRequest>();
                var sevenDaysAgo = DateTime.UtcNow.AddDays(-7);

                if (doc.RootElement.TryGetProperty("results", out var resultsArray))
                {
                    foreach (var item in resultsArray.EnumerateArray())
                    {
                        var status = item.GetProperty("status").GetInt32();
                        var updatedAt = item.GetProperty("updatedAt").GetString() ?? "";

                        DateTime updatedDate;
                        if (!DateTime.TryParse(updatedAt, out updatedDate))
                        {
                            updatedDate = DateTime.MinValue;
                        }

                        // Get media info
                        var mediaInfo = item.GetProperty("media");
                        var mediaStatus = mediaInfo.TryGetProperty("status", out var mediaStatusProp) ? mediaStatusProp.GetInt32() : 1;
                        var tmdbId = mediaInfo.GetProperty("tmdbId").GetInt32();
                        var mediaType = mediaInfo.TryGetProperty("mediaType", out var mediaTypeProp) ? mediaTypeProp.GetString() ?? "movie" : "movie";

                        // Filter: Include all in-flight (status 1=PENDING, 2=APPROVED) OR 
                        // completed/available (mediaStatus 4=PARTIALLY_AVAILABLE, 5=AVAILABLE) within last 7 days
                        bool isInFlight = status == 1 || status == 2;
                        bool isCompleted = mediaStatus == 4 || mediaStatus == 5;
                        bool isRecent = updatedDate >= sevenDaysAgo;

                        if (isInFlight || (isCompleted && isRecent))
                        {
                            // Fetch full media details using TMDB ID
                            var (title, posterPath) = await GetMediaDetailsAsync(tmdbId, mediaType, cancellationToken);

                            var request = new JellyseerrRequest
                            {
                                Id = item.GetProperty("id").GetInt32(),
                                Status = status,
                                Title = title,
                                MediaType = mediaType,
                                PosterPath = posterPath,
                                TmdbId = tmdbId,
                                CreatedAt = item.GetProperty("createdAt").GetString() ?? "",
                                UpdatedAt = updatedAt,
                                MediaInfoStatus = mediaStatus
                            };

                            requests.Add(request);
                        }
                    }
                }

                Console.WriteLine($"[JellyseerrService] Retrieved {requests.Count} active/recent requests");
                return requests;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JellyseerrService] Get requests error: {ex.Message}");
                return Array.Empty<JellyseerrRequest>();
            }
        }

        private async Task<(string title, string posterPath)> GetMediaDetailsAsync(int tmdbId, string mediaType, CancellationToken cancellationToken)
        {
            try
            {
                var endpoint = mediaType == "movie" ? $"api/v1/movie/{tmdbId}" : $"api/v1/tv/{tmdbId}";
                var response = await _httpClient.GetAsync(endpoint, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[JellyseerrService] Failed to get {mediaType} details for TMDB ID {tmdbId}: {response.StatusCode}");
                    return ($"Unknown {(mediaType == "movie" ? "Movie" : "Series")}", "");
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);

                var title = "";
                if (mediaType == "movie")
                {
                    title = doc.RootElement.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                }
                else
                {
                    title = doc.RootElement.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                }

                var posterPath = doc.RootElement.TryGetProperty("posterPath", out var posterProp) ? posterProp.GetString() ?? "" : "";

                return (title, posterPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JellyseerrService] Error fetching media details: {ex.Message}");
                return ($"Unknown {(mediaType == "movie" ? "Movie" : "Series")}", "");
            }
        }
    }
}
