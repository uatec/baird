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

        public Task InitializeAsync(string serverUrl, string username, string password)
        {
            _serverUrl = serverUrl.TrimEnd('/');
            _username = username;
            _password = password;

            var handler = new HttpClientHandler
            {
                Credentials = new System.Net.NetworkCredential(_username, _password)
            };
            
            _httpClient = new HttpClient(handler);
            _httpClient.BaseAddress = new Uri(_serverUrl + "/");

            Console.WriteLine($"Initialized TVHeadend Service at {_serverUrl} with Digest/Basic Auth support");
            return Task.CompletedTask;
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
                    return grid.Entries.Select(c => new MediaItem 
                    {
                        Id = c.Uuid,
                        Name = c.Name,
                        Details = c.Number.ToString(),
                        // TVHeadend icon URL: /imagecache/{id}
                        ImageUrl = !string.IsNullOrEmpty(c.IconUrl) ? c.IconUrl : $"{_serverUrl}/imagecache/{c.IconId}"
                    });
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

        public string GetStreamUrl(string itemId)
        {
            // Stream URL format: http://user:pass@host:port/stream/channel/{uuid}
            // We embed credentials in URL for mpv to handle auth easily
            
            // Parse host/port from serverUrl
            Uri uri = new Uri(_serverUrl);
            var host = uri.Host;
            var port = uri.Port;
            var scheme = uri.Scheme;
            
            return $"{scheme}://{_username}:{_password}@{host}:{port}/stream/channel/{itemId}";
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
