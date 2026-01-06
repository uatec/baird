using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Baird.Services
{
    public class TvHeadendService : IMediaProvider
    {
        private HttpClient _httpClient;
        private string _serverUrl;
        private string _username;
        private string _password;

        public async Task InitializeAsync()
        {
            // Load configuration locally
            string serverUrl = Environment.GetEnvironmentVariable("TVH_URL") ?? "http://localhost:9981";
            string username = Environment.GetEnvironmentVariable("TVH_USER") ?? "unknown";
            string password = Environment.GetEnvironmentVariable("TVH_PASS") ?? "unknown";

            // Support .env file if present
            if (System.IO.File.Exists(".env"))
            {
                foreach (var line in System.IO.File.ReadAllLines(".env"))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length != 2) continue;
                    var key = parts[0].Trim();
                    var val = parts[1].Trim();
                    
                    if (key == "TVH_URL") serverUrl = val;
                    if (key == "TVH_USER") username = val;
                    if (key == "TVH_PASS") password = val;
                }
            }

            _serverUrl = serverUrl.TrimEnd('/');
            _username = username;
            _password = password;

            Console.WriteLine($"Attempting connection to: {_serverUrl} as {_username}");

            // Configure Handler with Credentials for Digest/Basic Auth
            var handler = new HttpClientHandler 
            { 
                Credentials = new System.Net.NetworkCredential(_username, _password),
                PreAuthenticate = true // Helpful for Basic, but Digest requires challenge
            };

            _httpClient = new HttpClient(handler) { BaseAddress = new Uri(_serverUrl + "/") };
            
            Console.WriteLine($"Initialized TVHeadend Service at {_serverUrl} with Digest/Basic Auth support");
            await Task.CompletedTask;
        }

        public async Task<IEnumerable<MediaItem>> GetListingAsync()
        {
            try
            {
                // API to get channel grid: /api/channel/grid
                var url = "api/channel/grid?start=0&limit=9999";
                
                Console.WriteLine($"Fetching channels from: {url}");
                
                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                
                // Using Source Generator context
                var grid = JsonSerializer.Deserialize(json, TvHeadendJsonContext.Default.TvHeadendGrid);
                
                if (grid?.Entries != null)
                {
                    return grid.Entries
                        .Select(c => new MediaItem 
                        {
                            Id = c.Uuid,
                            Name = c.Name,
                            Details = "", // Channel number moved to ChannelNumber
                            ChannelNumber = c.Number.ToString(),
                            // TVHeadend icon URL: /imagecache/{id}
                            ImageUrl = !string.IsNullOrEmpty(c.IconUrl) ? c.IconUrl : $"{_serverUrl}/imagecache/{c.IconId}",
                            IsLive = true,
                            StreamUrl = GetStreamUrlInternal(c.Uuid),
                            Source = "Live TV"
                        })
                        .OrderBy(c => c.Name);
                }
                
                return Enumerable.Empty<MediaItem>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to fetch TV channels: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return Enumerable.Empty<MediaItem>();
            }
        }

        public async Task<IEnumerable<MediaItem>> SearchAsync(string query)
        {
            var all = await GetListingAsync();
            if (string.IsNullOrWhiteSpace(query)) return all;

            var q = query.Trim();

            // Use the same logic as before but inside the provider
            // Search by channel number first
            var channelMatches = all
                .Where(i => i.ChannelNumber != null && i.ChannelNumber.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.ChannelNumber.Length)
                .ThenBy(i => i.ChannelNumber);

            var nameMatches = all
                .Where(i => i.Name != null && i.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(i => i.ChannelNumber == null || !i.ChannelNumber.StartsWith(q, StringComparison.OrdinalIgnoreCase));

            return channelMatches.Concat(nameMatches);
        }

        public Task<IEnumerable<MediaItem>> GetChildrenAsync(string id)
        {
            return Task.FromResult(Enumerable.Empty<MediaItem>());
        }

        private string GetStreamUrlInternal(string itemId)
        {
            // Stream URL format: http://user:pass@host:port/stream/channel/{uuid}
            // We embed credentials in URL for mpv to handle auth easily
            
            // Parse host/port from serverUrl
            Uri uri = new Uri(_serverUrl);
            var host = uri.Host;
            var port = uri.Port;
            var scheme = uri.Scheme;
            
            return $"{scheme}://{host}:{port}/stream/channel/{itemId}?auth={_username}:{_password}";
        }
    }

    // Data models for TVHeadend JSON
    public class TvHeadendGrid
    {
        [JsonPropertyName("entries")]
        public TvHeadendEntry[] Entries { get; set; }
        
        [JsonPropertyName("totalCount")]
        public int TotalCount { get; set; }
    }

    public class TvHeadendEntry
    {
        [JsonPropertyName("uuid")]
        public string Uuid { get; set; }
        
        [JsonPropertyName("name")]
        public string Name { get; set; }
        
        [JsonPropertyName("number")]
        public int Number { get; set; }
        
        [JsonPropertyName("icon")]
        public string IconUrl { get; set; }
        
        [JsonPropertyName("icon_public_url")]
        public string IconId { get; set; } // Sometimes useful if IconUrl is relative
    }

    [JsonSerializable(typeof(TvHeadendGrid))]
    internal partial class TvHeadendJsonContext : JsonSerializerContext
    {
    }
}
