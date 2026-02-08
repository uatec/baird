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

        public async Task<IEnumerable<MediaItem>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<MediaItem>();

            try
            {
                var url = string.Format(SearchUrl, Uri.EscapeDataString(query));
                Console.WriteLine($"[BBCiPlayer] Searching: {url}");
                
                var response = await _httpClient.GetStringAsync(url, cancellationToken);
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
                        if (type == "episode" || type == "brand" || type == "programme")
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

                            string synopsis = "";
                            if (item.TryGetProperty("synopses", out var synopsesProp))
                            {
                                if (synopsesProp.TryGetProperty("medium", out var mediumSyn)) synopsis = mediumSyn.GetString() ?? "";
                                else if (synopsesProp.TryGetProperty("small", out var smallSyn)) synopsis = smallSyn.GetString() ?? "";
                            }

                            var mediaType = (type == "brand" || type == "programme") ? MediaType.Brand : MediaType.Video;

                            items.Add(new MediaItem
                            {
                                Id = id,
                                Name = title,
                                Details = subtitle,
                                ImageUrl = imageUrl,
                                IsLive = false,
                                Type = mediaType,
                                Synopsis = synopsis,
                                StreamUrl = mediaType == MediaType.Brand ? "" : $"https://www.bbc.co.uk/iplayer/episode/{id}",
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

        public async Task<IEnumerable<MediaItem>> GetChildrenAsync(string id)
        {
            try
            {
                // Fetch episodes for a brand/programme
                // Using per_page=100 to get a good chunk of episodes. Pagination not implemented for now.
                var url = $"https://ibl.api.bbci.co.uk/ibl/v1/programmes/{id}/episodes?per_page=100&rights=web&availability=available";
                Console.WriteLine($"[BBCiPlayer] Fetching episodes: {url}");

                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);
                
                if (!doc.RootElement.TryGetProperty("programmes", out var programmesProp)) // It seems the root key is based on the request? Or fixed?
                {
                    // Based on "https://ibl.api.bbci.co.uk/ibl/v1/programmes/{id}/episodes"
                    // The curl output from the user in Step 524 shows:
                    // {"version":"1.0", "programme_episodes": { ... "count": 12, "initial_children": [...] } } 
                    // WAIT, NO.
                    // The user's output in step 524 is truncated but starts with `{"version":"1.0","schema":"...`
                    // Let's look closer at the user's provided JSON in step 503 for a "programme" (brand).
                    // That was the SEARCH result structure.
                    
                    // The curl in Step 524 (fetching episodes) produced:
                    // {"version":"1.0","schema":"...","programme_episodes":{"id":"...","type":"programme","title":"...","initial_children":[...]}}
                    
                    // So the key is likely `programme_episodes`.
                    
                    if (!doc.RootElement.TryGetProperty("programme_episodes", out var progEpisodes))
                    {
                         // Try 'episodes' just in case
                         if(!doc.RootElement.TryGetProperty("episodes", out progEpisodes))
                         {
                             Console.WriteLine("[BBCiPlayer] 'programme_episodes' or 'episodes' property not found");
                             return Enumerable.Empty<MediaItem>();
                         }
                    }
                    
                    // The structure seems to be:
                    // programme_episodes -> initial_children (array) OR just direct elements?
                    // Re-reading user provided output for "programmes/{pid}/episodes"
                    // The output in 524 was: `... "initial_children": [ ... ]`
                    // So we look for `programme_episodes` -> `initial_children`?
                    // Actually, looking at the truncated output in 524, it looks like `programme_episodes` object contains `initial_children`. 
                    // Correction: The command in 524 was `.../programmes/p0d5z0xy/episodes...`
                    // The output shows `... "programme_episodes": { ... "initial_children": [ ... ] ... }`
                    
                    // Wait, let's look at the structure again.
                    // The output in 524 was: `... "programme_episodes": { ... "count": 12, "initial_children": [ ... ] }`
                    // So we iterate `initial_children`.

                    JsonElement elements;
                    if (progEpisodes.ValueKind == JsonValueKind.Array)
                    {
                        // Direct array?
                         elements = progEpisodes;
                    }
                    else if (progEpisodes.TryGetProperty("initial_children", out var initialChildren))
                    {
                        elements = initialChildren;
                    }
                    else if (progEpisodes.TryGetProperty("elements", out var elementsProp))
                    {
                        elements = elementsProp;
                    }
                    else 
                    {
                        Console.WriteLine("[BBCiPlayer] Could not find array of episodes");
                        return Enumerable.Empty<MediaItem>();
                    }

                    var items = new List<MediaItem>();
                    foreach(var item in elements.EnumerateArray())
                    {
                        if(item.GetProperty("type").GetString() == "episode")
                        {
                            var epId = item.GetProperty("id").GetString();
                            var title = item.GetProperty("title").GetString();
                            
                             string subtitle = "";
                            if (item.TryGetProperty("subtitle", out var subProp))
                            {
                                subtitle = subProp.GetString() ?? "";
                            }
                            else if (item.TryGetProperty("slice_subtitle", out var sliceSub))
                            {
                                subtitle = sliceSub.GetString() ?? "";
                            }

                             string synopsis = "";
                            if (item.TryGetProperty("synopses", out var synopsesProp))
                            {
                                if (synopsesProp.TryGetProperty("medium", out var mediumSyn)) synopsis = mediumSyn.GetString() ?? "";
                                else if (synopsesProp.TryGetProperty("small", out var smallSyn)) synopsis = smallSyn.GetString() ?? "";
                            }

                            string imageUrl = "";
                            if (item.TryGetProperty("images", out var imagesProp) && 
                                imagesProp.TryGetProperty("standard", out var standardProp))
                            {
                                imageUrl = standardProp.GetString()?.Replace("{recipe}", "480x270") ?? "";
                            }

                            items.Add(new MediaItem
                            {
                                Id = epId,
                                Name = title, // "SAS Rogue Heroes" (usually Series title for episodes?)
                                // Actually for episodes, title is often the show title, and subtitle is "Series X: Episode Y"
                                // The User provided example in 503:
                                // "title": "SAS Rogue Heroes"
                                // "subtitle": "Series 2: Episode 6"
                                // "slice_subtitle": "Episode 6"
                                // "editorial_subtitle": "6/6 The SAS return to Britain"
                                
                                // We might want to construct a better display name if Name is just the Show Title.
                                // But `MediaItem` has `Name` and `Details` (Subtitle). 
                                // Let's keep Name as "SAS Rogue Heroes" and Details/Subtitle as "Series 2: Episode 6".
                                
                                Details = subtitle,
                                Subtitle = subtitle, 
                                Synopsis = synopsis,
                                ImageUrl = imageUrl,
                                IsLive = false,
                                Type = MediaType.Video,
                                StreamUrl = $"https://www.bbc.co.uk/iplayer/episode/{epId}",
                                Source = "BBC iPlayer"
                            });
                        }
                    }
                    return items;
                }
                
                return Enumerable.Empty<MediaItem>();
            }
            catch(Exception ex)
            {
                Console.WriteLine($"[BBCiPlayer] Failed to get children for {id}: {ex.Message}");
                return Enumerable.Empty<MediaItem>();
            }
    }
}

}