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
            SecretRedactor.Register("JELLYSEERR_API_KEY", _apiKey);

            _httpClient = new HttpClient();
            _httpClient.BaseAddress = new Uri(_baseUrl + "/");

            if (!string.IsNullOrEmpty(_apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
            }

            Console.WriteLine($"[JellyseerrService] Configured for {SecretRedactor.Redact(_baseUrl)}");
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
                var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[JellyseerrService] Search failed: {response.StatusCode}");
                    return Array.Empty<JellyseerrSearchResult>();
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseSearchResults(json, "Search");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JellyseerrService] Search error: {ex.Message}");
                return Array.Empty<JellyseerrSearchResult>();
            }
        }

        public async Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int page, CancellationToken cancellationToken = default)
        {
            try
            {
                var url = $"api/v1/discover/trending?page={page}";
                var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[JellyseerrService] Get trending failed: {response.StatusCode}");
                    return Array.Empty<JellyseerrSearchResult>();
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseSearchResults(json, "Trending");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JellyseerrService] Get trending error: {ex.Message}");
                return Array.Empty<JellyseerrSearchResult>();
            }
        }

        /// <summary>Seerr's discover URL for what's popular on one streaming provider in the UK.</summary>
        public static string DiscoverUrl(int providerId, string mediaType) =>
            $"api/v1/discover/{(mediaType == "tv" ? "tv" : "movies")}?watchProviders={providerId}&watchRegion=GB&sortBy=popularity.desc&page=1";

        public async Task<IEnumerable<JellyseerrSearchResult>> GetProviderTrendingAsync(int providerId, string mediaType, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await _httpClient.GetAsync(DiscoverUrl(providerId, mediaType), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[JellyseerrService] Discover {mediaType} for provider {providerId} failed: {response.StatusCode}");
                    return Array.Empty<JellyseerrSearchResult>();
                }
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseSearchResults(json, $"Discover {mediaType} {providerId}", mediaType);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JellyseerrService] Discover {mediaType} for provider {providerId} error: {ex.Message}");
                return Array.Empty<JellyseerrSearchResult>();
            }
        }

        /// <summary>Discover results carry no "mediaType" (trending ones do), so the caller says which it asked for.</summary>
        internal static List<JellyseerrSearchResult> ParseSearchResults(string json, string source, string? defaultMediaType = null)
        {
            using var doc = JsonDocument.Parse(json);

            var results = new List<JellyseerrSearchResult>();

            if (doc.RootElement.TryGetProperty("results", out var resultsArray))
            {
                foreach (var item in resultsArray.EnumerateArray())
                {
                    var mediaType = item.TryGetProperty("mediaType", out var mt) ? mt.GetString() ?? "" : defaultMediaType ?? "";

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

            Console.WriteLine($"[JellyseerrService] {source} returned {results.Count} results");
            return results;
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

                var json = JsonSerializer.Serialize(requestBody, BairdJsonContext.Default.DictionaryStringObject);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync("api/v1/request", content, cancellationToken).ConfigureAwait(false);
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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
                    Console.WriteLine($"[JellyseerrService] Request failed: {response.StatusCode} - {SecretRedactor.Redact(responseJson)}");
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

        public async Task<JellyseerrMediaStatus?> GetMediaStatusAsync(int tmdbId, string mediaType, CancellationToken cancellationToken = default)
        {
            try
            {
                var endpoint = mediaType == "movie" ? $"api/v1/movie/{tmdbId}" : $"api/v1/tv/{tmdbId}";
                var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[JellyseerrService] Status of {mediaType} {tmdbId} failed: {response.StatusCode}");
                    return null;
                }
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseMediaStatus(json);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"[JellyseerrService] Status of {mediaType} {tmdbId} error: {ex.Message}");
                return null;
            }
        }

        /// <summary>Reads a movie/tv details response: TMDB's seasons (specials excluded), mediaInfo's per-season status and downloads.</summary>
        public static JellyseerrMediaStatus ParseMediaStatus(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            static int Int(JsonElement e, string name, int fallback = 0) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
            static long Long(JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

            var status = 1;
            var seasonStatus = new Dictionary<int, int>();
            var downloads = new List<JellyseerrDownload>();
            if (root.TryGetProperty("mediaInfo", out var info) && info.ValueKind == JsonValueKind.Object)
            {
                status = Int(info, "status", 1);
                if (info.TryGetProperty("seasons", out var ss) && ss.ValueKind == JsonValueKind.Array)
                    foreach (var s in ss.EnumerateArray()) seasonStatus[Int(s, "seasonNumber")] = Int(s, "status", 1);
                if (info.TryGetProperty("downloadStatus", out var ds) && ds.ValueKind == JsonValueKind.Array)
                    foreach (var d in ds.EnumerateArray())
                    {
                        int? season = null, episode = null;
                        if (d.TryGetProperty("episode", out var ep) && ep.ValueKind == JsonValueKind.Object)
                        {
                            season = Int(ep, "seasonNumber");
                            episode = Int(ep, "episodeNumber");
                        }
                        downloads.Add(new JellyseerrDownload(season, episode, Long(d, "size"), Long(d, "sizeLeft")));
                    }
            }

            var seasons = new List<JellyseerrSeason>();
            if (root.TryGetProperty("seasons", out var tmdbSeasons) && tmdbSeasons.ValueKind == JsonValueKind.Array)
                foreach (var s in tmdbSeasons.EnumerateArray())
                {
                    var n = Int(s, "seasonNumber");
                    if (n <= 0) continue; // specials
                    seasons.Add(new JellyseerrSeason(n, Int(s, "episodeCount"), seasonStatus.TryGetValue(n, out var st) ? st : 1));
                }
            var overview = root.TryGetProperty("overview", out var ov) && ov.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ov.GetString()) ? ov.GetString() : null;
            return new JellyseerrMediaStatus(status, seasons, downloads, overview);
        }

        public async Task<JellyseerrRequestResponse> RequestSeasonsAsync(int mediaId, string mediaType, IReadOnlyList<int>? seasons, CancellationToken cancellationToken = default)
        {
            if (mediaType != "tv" || seasons == null || seasons.Count == 0) return await CreateRequestAsync(mediaId, mediaType, cancellationToken).ConfigureAwait(false);
            try
            {
                var json = $"{{\"mediaType\":\"tv\",\"mediaId\":{mediaId},\"seasons\":[{string.Join(",", seasons)}]}}";
                var response = await _httpClient.PostAsync("api/v1/request", new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[JellyseerrService] Season request failed: {response.StatusCode} - {SecretRedactor.Redact(body)}");
                    return new JellyseerrRequestResponse { Success = false, Message = $"Failed to create request: {response.StatusCode}" };
                }
                using var doc = JsonDocument.Parse(body);
                var id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                Console.WriteLine($"[JellyseerrService] Requested seasons {string.Join(",", seasons)} of tv {mediaId} (request {id})");
                return new JellyseerrRequestResponse { Success = true, Message = "Requested", RequestId = id };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JellyseerrService] Season request error: {ex.Message}");
                return new JellyseerrRequestResponse { Success = false, Message = $"Error: {ex.Message}" };
            }
        }

        public async Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Fetch recent requests sorted by modified date
                var url = "api/v1/request?sort=modified&sortDirection=desc&take=100";
                var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[JellyseerrService] Get requests failed: {response.StatusCode}");
                    return Array.Empty<JellyseerrRequest>();
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);

                var wanted = new List<(int Id, int Status, int MediaId, int TmdbId, string MediaType, int MediaStatus, string CreatedAt, string UpdatedAt)>();
                var recentSince = DateTime.UtcNow.AddDays(-14); // Home's Downloads row keeps arrived titles for two weeks

                if (doc.RootElement.TryGetProperty("results", out var resultsArray))
                {
                    foreach (var item in resultsArray.EnumerateArray())
                    {
                        var status = item.GetProperty("status").GetInt32();
                        var updatedAt = item.GetProperty("updatedAt").GetString() ?? "";
                        if (!DateTime.TryParse(updatedAt, out var updatedDate)) updatedDate = DateTime.MinValue;

                        var mediaInfo = item.GetProperty("media");
                        var mediaStatus = mediaInfo.TryGetProperty("status", out var mediaStatusProp) ? mediaStatusProp.GetInt32() : 1;
                        var tmdbId = mediaInfo.GetProperty("tmdbId").GetInt32();
                        var mediaId = mediaInfo.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.Number ? mid.GetInt32() : 0;
                        var mediaType = mediaInfo.TryGetProperty("mediaType", out var mediaTypeProp) ? mediaTypeProp.GetString() ?? "movie" : "movie";

                        // Filter: Include all in-flight (status 1=PENDING, 2=APPROVED) OR
                        // completed/available (mediaStatus 4=PARTIALLY_AVAILABLE, 5=AVAILABLE) within the last 14 days
                        bool isInFlight = status == 1 || status == 2;
                        bool isCompleted = mediaStatus == 4 || mediaStatus == 5;
                        if (isInFlight || (isCompleted && updatedDate >= recentSince))
                            wanted.Add((item.GetProperty("id").GetInt32(), status, mediaId, tmdbId, mediaType, mediaStatus,
                                item.GetProperty("createdAt").GetString() ?? "", updatedAt));
                    }
                }

                // Each title's details (name, poster, release date, seasons, downloads): a few at a time, not one by one.
                using var gate = new SemaphoreSlim(4);
                var requests = await Task.WhenAll(wanted.Select(async w =>
                {
                    await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var d = await GetMediaDetailsAsync(w.TmdbId, w.MediaType, cancellationToken).ConfigureAwait(false);
                        return new JellyseerrRequest
                        {
                            Id = w.Id,
                            Status = w.Status,
                            MediaId = w.MediaId,
                            Title = d.Title,
                            MediaType = w.MediaType,
                            PosterPath = d.PosterPath,
                            TmdbId = w.TmdbId,
                            CreatedAt = w.CreatedAt,
                            UpdatedAt = w.UpdatedAt,
                            MediaInfoStatus = w.MediaStatus,
                            DownloadProgress = d.Status?.Progress() is { } p ? p * 100 : null,
                            ReleaseDate = d.ReleaseDate,
                            Seasons = d.Status?.Seasons ?? Array.Empty<JellyseerrSeason>(),
                        };
                    }
                    finally { gate.Release(); }
                })).ConfigureAwait(false);

                Console.WriteLine($"[JellyseerrService] Retrieved {requests.Length} active/recent requests");
                return requests;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JellyseerrService] Get requests error: {ex.Message}");
                return Array.Empty<JellyseerrRequest>();
            }
        }

        private async Task<(string Title, string PosterPath, string? ReleaseDate, JellyseerrMediaStatus? Status)> GetMediaDetailsAsync(int tmdbId, string mediaType, CancellationToken cancellationToken)
        {
            var unknown = ($"Unknown {(mediaType == "movie" ? "Movie" : "Series")}", "", (string?)null, (JellyseerrMediaStatus?)null);
            try
            {
                var endpoint = mediaType == "movie" ? $"api/v1/movie/{tmdbId}" : $"api/v1/tv/{tmdbId}";
                var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[JellyseerrService] Failed to get {mediaType} details for TMDB ID {tmdbId}: {response.StatusCode}");
                    return unknown;
                }
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var title = (mediaType == "movie" ? Str("title") : Str("name")) ?? "";
                var released = mediaType == "movie" ? Str("releaseDate") : Str("firstAirDate");
                return (title, Str("posterPath") ?? "", string.IsNullOrEmpty(released) ? null : released, ParseMediaStatus(json));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"[JellyseerrService] Error fetching media details: {ex.Message}");
                return unknown;
            }
        }

        public async Task<(bool Ok, string Message)> RemoveMediaAsync(int tmdbId, string mediaType, CancellationToken cancellationToken = default)
        {
            try
            {
                var endpoint = mediaType == "movie" ? $"api/v1/movie/{tmdbId}" : $"api/v1/tv/{tmdbId}";
                var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return (false, $"Seerr couldn't find it ({(int)response.StatusCode}).");
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("mediaInfo", out var info) || info.ValueKind != JsonValueKind.Object
                    || !info.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
                    return (true, "Seerr has nothing for it.");
                var mediaId = idProp.GetInt32();

                // Radarr/Sonarr: remove it and its files, so nothing fetches it again.
                var files = await _httpClient.DeleteAsync($"api/v1/media/{mediaId}/file?is4k=false", cancellationToken).ConfigureAwait(false);
                if (!files.IsSuccessStatusCode)
                {
                    var body = await files.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    Console.WriteLine($"[JellyseerrService] Removing {mediaType} {tmdbId} from Radarr/Sonarr failed: {files.StatusCode} - {SecretRedactor.Redact(body)}");
                    return (false, $"Radarr/Sonarr didn't remove it ({(int)files.StatusCode}).");
                }
                // Seerr: forget it, so it can be requested again.
                var media = await _httpClient.DeleteAsync($"api/v1/media/{mediaId}", cancellationToken).ConfigureAwait(false);
                if (!media.IsSuccessStatusCode)
                    Console.WriteLine($"[JellyseerrService] Clearing {mediaType} {tmdbId} from Seerr failed: {media.StatusCode}");
                Console.WriteLine($"[JellyseerrService] Removed {mediaType} {tmdbId} (media {mediaId}) and its files");
                return (true, "Removed");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"[JellyseerrService] Removing {mediaType} {tmdbId} error: {ex.Message}");
                return (false, ex.Message);
            }
        }
    }
}
