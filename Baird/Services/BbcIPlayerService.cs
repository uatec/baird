using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Baird.Services
{
    public class BbcIPlayerService : IMediaProvider
    {
        private static readonly HttpClient _httpClient;
        private const string SearchUrl = "https://ibl.api.bbci.co.uk/ibl/v1/new-search?q={0}&rights=web&mixin=live&lang=en&availability=available";

        static BbcIPlayerService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        public Task InitializeAsync()
        {
            Console.WriteLine("BBC iPlayer Service Initialized");
            return Task.CompletedTask;
        }

        public Task<IEnumerable<MediaItem>> GetListingAsync()
        {
            // Full browsing not implemented yet
            return Task.FromResult(Enumerable.Empty<MediaItem>());
        }

        public async Task<IEnumerable<MediaItem>> SearchAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<MediaItem>();

            try
            {
                var url = string.Format(SearchUrl, Uri.EscapeDataString(query));
                Console.WriteLine($"[BBCiPlayer] Searching: {url}");
                
                var response = await _httpClient.GetStringAsync(url);
                Console.WriteLine($"[BBCiPlayer] Response received ({response.Length} chars)");

                using var doc = JsonDocument.Parse(response);
                if (!doc.RootElement.TryGetProperty("new_search", out var newSearch))
                {
                    Console.WriteLine("[BBCiPlayer] 'new_search' property not found in JSON");
                    return Enumerable.Empty<MediaItem>();
                }

                if (!newSearch.TryGetProperty("results", out var results))
                {
                    Console.WriteLine("[BBCiPlayer] 'results' property not found in JSON");
                    return Enumerable.Empty<MediaItem>();
                }

                var items = new List<MediaItem>();
                Console.WriteLine($"[BBCiPlayer] Found {results.GetArrayLength()} results");

                foreach (var item in results.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var typeProp))
                    {
                        var type = typeProp.GetString();
                        if (type == "episode" || type == "brand")
                        {
                            var id = item.GetProperty("id").GetString();
                            var title = item.GetProperty("title").GetString();
                            
                            string subtitle = "";
                            if (item.TryGetProperty("subtitle", out var subProp))
                            {
                                subtitle = subProp.GetString() ?? "";
                            }

                            string imageUrl = "";
                            if (item.TryGetProperty("images", out var imagesProp) && 
                                imagesProp.TryGetProperty("standard", out var standardProp))
                            {
                                imageUrl = standardProp.GetString()?.Replace("{recipe}", "480x270") ?? "";
                            }

                            items.Add(new MediaItem
                            {
                                Id = id,
                                Name = title,
                                Details = subtitle,
                                ImageUrl = imageUrl,
                                IsLive = false,
                                StreamUrl = $"https://www.bbc.co.uk/iplayer/episode/{id}",
                                Source = "BBC iPlayer"
                            });
                        }
                        else
                        {
                             Console.WriteLine($"[BBCiPlayer] Skipping item of type: {type}");
                        }
                    }
                }

                Console.WriteLine($"[BBCiPlayer] Returning {items.Count} items");
                return items;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BBCiPlayer] Search failed: {ex.Message}");
                if (ex.InnerException != null) Console.WriteLine($"[BBCiPlayer] Inner: {ex.InnerException.Message}");
                return Enumerable.Empty<MediaItem>();
            }
        }
    }
}
