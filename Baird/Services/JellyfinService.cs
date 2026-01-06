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
        private string _serverUrl;
        private HttpClient _httpClient;
        private HttpClientRequestAdapter _requestAdapter;
        private string _serverHostname = "Jellyfin";
        private string _accessToken;
        private string _userId;
        private string _username;

        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);

        public async Task InitializeAsync()
        {
            // Load configuration locally
            string serverUrl = Environment.GetEnvironmentVariable("JELLYFIN_URL") ?? "http://localhost:8096";
            string username = Environment.GetEnvironmentVariable("JELLYFIN_USER") ?? "unknown";
            string password = Environment.GetEnvironmentVariable("JELLYFIN_PASS") ?? "unknown";

            // Support .env file if present
            if (System.IO.File.Exists(".env"))
            {
                foreach (var line in System.IO.File.ReadAllLines(".env"))
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
            try { _serverHostname = new Uri(_serverUrl).Host; } catch { }
            _username = username;
            _userId = ""; // Will be set after auth
            // Password not stored permanently unless needed for re-auth
            
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

            // _client = new JellyfinApiClient(_requestAdapter); // Not strictly needed if we go full manual

            // 2. Authenticate (Manual Implementation)
            try 
            {
                await AuthenticateAsync(username, password);
                Console.WriteLine($"Initialized Jellyfin Service at {_serverUrl}");
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
                 _accessToken = tokenProp.GetString();
             }
             
             if (root.TryGetProperty("User", out var userProp) && userProp.TryGetProperty("Id", out var idProp))
             {
                 _userId = idProp.GetString();
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
            if (!IsAuthenticated) return Enumerable.Empty<MediaItem>();

            try
            {
                // Manual HTTP GET for Items
                // Endpoint: /Users/{UserId}/Items
                var url = $"Users/{_userId}/Items?IncludeItemTypes=Movie&Recursive=true&SortBy=SortName&Fields=ProductionYear";
                
                Console.WriteLine($"Fetching movies from: {url}");
                
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize(json, AppJsonContext.Default.MovieQueryResult);
                
                if (result?.Items != null)
                {
                    return result.Items.Select(m => new MediaItem 
                    {
                        Id = m.Id,
                        Name = m.Name,
                        Details = m.ProductionYear?.ToString() ?? "Unknown Year",
                        ImageUrl = $"{_serverUrl.TrimEnd('/')}/Items/{m.Id}/Images/Primary?api_key={_accessToken}",
                        IsLive = false,
                        StreamUrl = GetStreamUrlInternal(m.Id),
                        Source = $"Jellyfin: {_serverHostname}"
                    });
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

        public async Task<IEnumerable<MediaItem>> SearchAsync(string query)
        {
            if (!IsAuthenticated || string.IsNullOrWhiteSpace(query)) 
                return await GetListingAsync();

            try
            {
                var q = Uri.EscapeDataString(query.Trim());
                var url = $"Users/{_userId}/Items?IncludeItemTypes=Movie&Recursive=true&SortBy=SortName&Fields=ProductionYear&SearchTerm={q}";
                
                Console.WriteLine($"Searching Jellyfin movies with query '{query}': {url}");
                
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize(json, AppJsonContext.Default.MovieQueryResult);
                
                if (result?.Items != null)
                {
                    return result.Items.Select(m => new MediaItem 
                    {
                        Id = m.Id,
                        Name = m.Name,
                        Details = m.ProductionYear?.ToString() ?? "Unknown Year",
                        ImageUrl = $"{_serverUrl.TrimEnd('/')}/Items/{m.Id}/Images/Primary?api_key={_accessToken}",
                        IsLive = false,
                        StreamUrl = GetStreamUrlInternal(m.Id),
                        Source = $"Jellyfin: {_serverHostname}"
                    });
                }
                return Enumerable.Empty<MediaItem>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Jellyfin search failed: {ex.Message}");
                return Enumerable.Empty<MediaItem>();
            }
        }

        public Task<IEnumerable<MediaItem>> GetChildrenAsync(string id)
        {
            // Could implement folder browsing here later
            return Task.FromResult(Enumerable.Empty<MediaItem>());
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
                if(request.Content != null)
                {
                     var content = await request.Content.ReadAsStringAsync();
                    //  Console.WriteLine($"REQ BODY: {content}");
                }

                var response = await base.SendAsync(request, cancellationToken);

                // Console.WriteLine($"RESP: {response.StatusCode}");
                if(response.Content != null)
                {
                     var content = await response.Content.ReadAsStringAsync();
                    //  Console.WriteLine($"RESP BODY: {content}");
                     // Re-create content so it can be read again
                     response.Content = new StringContent(content, System.Text.Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "application/json");
                     foreach(var h in response.Headers) response.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                return response;
            }
        }
    }

    // Source Generator Support Types
    public class AuthRequest
    {
        public string Username { get; set; }
        public string Pw { get; set; }
    }

    public class MovieQueryResult
    {
        public MovieItem[] Items { get; set; }
        public int TotalRecordCount { get; set; }
    }

    public class MovieItem
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public int? ProductionYear { get; set; }
    }

    [JsonSerializable(typeof(AuthRequest))]
    [JsonSerializable(typeof(MovieQueryResult))]
    internal partial class AppJsonContext : JsonSerializerContext
    {
    }
}
