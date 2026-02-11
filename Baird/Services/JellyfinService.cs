using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Jellyfin.Sdk; // Keeping for now if minimal types needed, but trying to avoid usage

namespace Baird.Services
{
    public class JellyfinService : IMediaProvider
    {
        private readonly string _serverUrl;
        private readonly HttpClient _httpClient;
        private readonly HttpClientRequestAdapter _requestAdapter;
        private readonly string _serverHostname;
        private readonly string _username;
        private readonly string _password; // Store for lazy auth
        private string _accessToken = "";
        private string _userId = "";
        private bool _authenticationAttempted = false;

        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);

        public JellyfinService()
        {
            // Load configuration locally
            string serverUrl = Environment.GetEnvironmentVariable("JELLYFIN_URL") ?? "http://localhost:8096";
            string username = Environment.GetEnvironmentVariable("JELLYFIN_USER") ?? "unknown";
            string password = Environment.GetEnvironmentVariable("JELLYFIN_PASS") ?? "unknown";

            // Support .env file if present (check current and parent dir)
            var envPath = ".env";
            if (!System.IO.File.Exists(envPath))
            {
                if (System.IO.File.Exists("../.env")) envPath = "../.env";
            }

            if (System.IO.File.Exists(envPath))
            {
                foreach (var line in System.IO.File.ReadAllLines(envPath))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;
                    var key = parts[0].Trim();
                    var val = parts[1].Trim();

                    if (key == "JELLYFIN_URL") serverUrl = val;
                    if (key == "JELLYFIN_USER") username = val;
                    if (key == "JELLYFIN_PASS") password = val;
                }
            }

            _serverUrl = serverUrl.TrimEnd('/');
            try { _serverHostname = new Uri(_serverUrl).Host; } catch { _serverHostname = "Jellyfin"; }
            _username = username;
            _password = password; // Store for lazy authentication

            // 1. Setup Request Adapter with Debug Logging
            var authProvider = new AnonymousAuthenticationProvider();

            // Keep the debug handler for visibility
            _httpClient = new HttpClient(new DebugHttpHandler());
            // Configure base address so relative URLs work
            _httpClient.BaseAddress = new Uri(_serverUrl + "/");

            _requestAdapter = new HttpClientRequestAdapter(authProvider, null, null, _httpClient);
            _requestAdapter.BaseUrl = _serverUrl;

            // Set default client headers for Jellyfin (required for Auth)
            var authHeader = $"MediaBrowser Client=\"Baird Media Player\", Device=\"Baird Device\", DeviceId=\"{Guid.NewGuid()}\", Version=\"1.0.0\"";

            // Note: HttpClient DefaultRequestHeaders are persistent.
            if (!_httpClient.DefaultRequestHeaders.Contains("X-Emby-Authorization"))
            {
                _httpClient.DefaultRequestHeaders.Add("X-Emby-Authorization", authHeader);
            }

            Console.WriteLine($"JellyfinService configured for {_serverUrl} (authentication will happen on first use)");
        }

        private async Task EnsureAuthenticatedAsync()
        {
            if (_authenticationAttempted) return;
            _authenticationAttempted = true;

            try
            {
                await AuthenticateAsync(_username, _password);
                Console.WriteLine($"Jellyfin authenticated at {_serverUrl}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Jellyfin Authentication Failed: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
                throw;
            }
        }

        private async Task AuthenticateAsync(string username, string password)
        {
            Console.WriteLine("Authenticating via manual HTTP request...");

            var authUrl = $"{_serverUrl}/Users/AuthenticateByName";
            // Use Source Generated serialization to support AOT/Trimming
            var authBody = new AuthRequest { Username = username, Pw = password };
            var jsonBody = JsonSerializer.Serialize(authBody, AppJsonContext.Default.AuthRequest);

            var request = new HttpRequestMessage(HttpMethod.Post, authUrl);
            request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var respContent = await response.Content.ReadAsStringAsync();

            // Parse manually
            using var doc = JsonDocument.Parse(respContent);
            var root = doc.RootElement;

            if (root.TryGetProperty("AccessToken", out var tokenProp))
            {
                _accessToken = tokenProp.GetString() ?? "";
            }

            if (root.TryGetProperty("User", out var userProp) && userProp.TryGetProperty("Id", out var idProp))
            {
                _userId = idProp.GetString() ?? "";
            }

            Console.WriteLine($"Auth Success. Token: {_accessToken?.Substring(0, 5)}... User: {_userId}");

            // 3. Update Headers with Token
            _httpClient.DefaultRequestHeaders.Remove("X-Emby-Authorization");
            // Set default client headers for Jellyfin (required for Auth)
            var authHeader = $"MediaBrowser Client=\"Baird Media Player\", Device=\"Baird Device\", DeviceId=\"{Guid.NewGuid()}\", Version=\"1.0.0\"";
            var authHeaderWithToken = $"{authHeader}, Token=\"{_accessToken}\"";
            _httpClient.DefaultRequestHeaders.Add("X-Emby-Authorization", authHeaderWithToken);
            _httpClient.DefaultRequestHeaders.Add("X-Emby-Token", _accessToken);
        }

        public async Task<IEnumerable<MediaItem>> GetListingAsync()
        {
            await EnsureAuthenticatedAsync();
            if (!IsAuthenticated) return Enumerable.Empty<MediaItem>();

            try
            {
                // Manual HTTP GET for Items
                // Endpoint: /Users/{UserId}/Items
                var url = $"Users/{_userId}/Items?IncludeItemTypes=Movie,Series,Episode&Recursive=true&SortBy=SortName&Fields=ProductionYear,ProviderIds,RunTimeTicks";

                Console.WriteLine($"Fetching movies and shows from: {url}");

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize(json, AppJsonContext.Default.MovieQueryResult);

                if (result?.Items != null)
                {
                    return result.Items.Select(MapJellyfinItem);
                }
                return Enumerable.Empty<MediaItem>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch movies: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return Enumerable.Empty<MediaItem>();
            }
        }

        public async Task<IEnumerable<MediaItem>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default)
        {
            await EnsureAuthenticatedAsync();
            if (!IsAuthenticated || string.IsNullOrWhiteSpace(query))
                return await GetListingAsync();

            try
            {
                var q = Uri.EscapeDataString(query.Trim());
                var url = $"Users/{_userId}/Items?IncludeItemTypes=Movie,Series&Recursive=true&SortBy=SortName&Fields=ProductionYear,RunTimeTicks&SearchTerm={q}";

                Console.WriteLine($"Searching Jellyfin movies and shows with query '{query}': {url}");

                var response = await _httpClient.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize(json, AppJsonContext.Default.MovieQueryResult);

                if (result?.Items != null)
                {
                    return result.Items.Select(MapJellyfinItem);
                }
                return Enumerable.Empty<MediaItem>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Jellyfin search failed: {ex.Message}");
                return Enumerable.Empty<MediaItem>();
            }
        }

        public async Task<IEnumerable<MediaItem>> GetChildrenAsync(string id)
        {
            await EnsureAuthenticatedAsync();
            if (!IsAuthenticated) return Enumerable.Empty<MediaItem>();

            try
            {
                // Parse ID for Season filtering (Format: itemId|seasonNumber)
                string itemId = id;
                string? targetSeason = null;

                if (id.Contains("|"))
                {
                    var parts = id.Split('|');
                    itemId = parts[0];
                    if (parts.Length > 1) targetSeason = parts[1];
                }

                // Changed to non-recursive to support Season folders
                // IncludeItemTypes: Season, Episode, Movie, Folder (for other structures)
                // Added ParentIndexNumber to Fields for manual grouping
                var url = $"Users/{_userId}/Items?ParentId={itemId}&IncludeItemTypes=Season,Episode,Movie,Folder&Recursive=false&SortBy=ParentIndexNumber,IndexNumber,SortName&Fields=ProductionYear,ParentIndexNumber,RunTimeTicks";
                // Console.WriteLine($"[Jellyfin] Fetching children for {itemId} with URL: {url}");

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();

                var result = JsonSerializer.Deserialize(json, AppJsonContext.Default.MovieQueryResult);

                if (result?.Items == null) return Enumerable.Empty<MediaItem>();

                var items = result.Items;

                // 1. If we are looking for a SPECIFIC Virtual Season (targetSeason != null)
                if (targetSeason != null)
                {
                    if (int.TryParse(targetSeason, out int seasonNum))
                    {
                        return items
                           .Where(i => (i.ParentIndexNumber ?? 1) == seasonNum)
                           .Select(MapJellyfinItem);
                    }
                    return Enumerable.Empty<MediaItem>();
                }

                // 2. If we found actual Seasons/Folders, just return them (Native Structure)
                if (items.Any(i => i.Type.Equals("Season", StringComparison.OrdinalIgnoreCase) ||
                                   i.Type.Equals("Folder", StringComparison.OrdinalIgnoreCase)))
                {
                    return items.Select(MapJellyfinItem);
                }

                // 3. We found only Episodes (flat library). Check if we need to group them.
                var seasons = items
                    .Select(i => i.ParentIndexNumber ?? 1)
                    .Distinct()
                    .OrderBy(x => x)
                    .ToList();

                if (seasons.Count > 1)
                {
                    // Create Virtual Season Folders
                    return seasons.Select(s => new MediaItem
                    {
                        Id = $"{itemId}|{s}",
                        Name = $"Season {s}",
                        Details = $"{items.Count(x => (x.ParentIndexNumber ?? 1) == s)} Episodes",
                        // Use Series image for the folder
                        ImageUrl = $"{_serverUrl.TrimEnd('/')}/Items/{itemId}/Images/Primary?api_key={_accessToken}",
                        Type = MediaType.Brand,
                        Source = $"Jellyfin: {_serverHostname}",
                        IsLive = false,
                        Synopsis = "Season " + s,
                        Subtitle = "Season " + s,  // TODO: should this stuff just be nullable or expicitly empty? or a subtype for Groupings
                    });
                }

                // 4. Otherwise (Single season or just episodes), return as is
                return items.Select(MapJellyfinItem);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Jellyfin GetChildren failed: {ex.Message}");
                return Enumerable.Empty<MediaItem>();
            }
        }

        private MediaItem MapJellyfinItem(MovieItem m)
        {
            // Treat Series, Season, and generic Folder as "Brand" (navigable container)
            var isContainer = m.Type != null && (
                m.Type.Equals("Series", StringComparison.OrdinalIgnoreCase) ||
                m.Type.Equals("Season", StringComparison.OrdinalIgnoreCase) ||
                m.Type.Equals("Folder", StringComparison.OrdinalIgnoreCase)
            );

            var type = isContainer ? MediaType.Brand : MediaType.Video;

            // Console.WriteLine($"[Jellyfin] Mapping: {m.Name}, Type={m.Type} -> {type}");

            // Convert RunTimeTicks to TimeSpan
            // Jellyfin uses ticks where 1 tick = 100 nanoseconds = 0.0000001 seconds
            // So we divide by 10,000,000 to get seconds
            var duration = m.RunTimeTicks.HasValue
                ? TimeSpan.FromTicks(m.RunTimeTicks.Value)
                : TimeSpan.Zero;

            return new MediaItem
            {
                Id = m.Id,
                Name = m.Name,
                Details = m.ProductionYear?.ToString() ?? "",
                ImageUrl = $"{_serverUrl.TrimEnd('/')}/Items/{m.Id}/Images/Primary?api_key={_accessToken}",
                IsLive = false,
                StreamUrl = isContainer ? "" : GetStreamUrlInternal(m.Id),
                Source = $"Jellyfin: {_serverHostname}",
                Type = type,
                Subtitle = "",
                Synopsis = "", // TODO: should this stuff just be nullable or expicitly empty? or a subtype for Groupings
                Duration = duration,
            };
        }

        private string GetStreamUrlInternal(string itemId)
        {
            return $"{_serverUrl}/Videos/{itemId}/stream?api_key={_accessToken}&static=true";
        }

        // Inner Debug Handler
        private class DebugHttpHandler : DelegatingHandler
        {
            public DebugHttpHandler() : base(new HttpClientHandler()) { }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                // Console.WriteLine($"REQ: {request.Method} {request.RequestUri}");
                if (request.Content != null)
                {
                    var content = await request.Content.ReadAsStringAsync();
                    //  Console.WriteLine($"REQ BODY: {content}");
                }

                var response = await base.SendAsync(request, cancellationToken);

                // Console.WriteLine($"RESP: {response.StatusCode}");
                if (response.Content != null)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    //  Console.WriteLine($"RESP BODY: {content}");
                    // Re-create content so it can be read again
                    response.Content = new StringContent(content, System.Text.Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "application/json");
                    foreach (var h in response.Headers) response.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                return response;
            }
        }
    }

    // Source Generator Support Types
    public class AuthRequest
    {
        public string Username { get; set; } = null!;
        public string Pw { get; set; } = null!;
    }

    public class MovieQueryResult
    {
        public MovieItem[] Items { get; set; } = null!;
        public int TotalRecordCount { get; set; }
    }

    public class MovieItem
    {
        public string Name { get; set; } = null!;
        public string Id { get; set; } = null!;
        public int? ProductionYear { get; set; }
        public string Type { get; set; } = null!;
        public int? ParentIndexNumber { get; set; }
        public long? RunTimeTicks { get; set; }
    }

    [JsonSerializable(typeof(AuthRequest))]
    [JsonSerializable(typeof(MovieQueryResult))]
    internal partial class AppJsonContext : JsonSerializerContext
    {
    }
}
