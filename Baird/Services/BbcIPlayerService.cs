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
                        if (type == "brand" || type == "programme")
                        {
                            var id = item.GetProperty("id").GetString();
                            if (id == null)
                            {
                                Console.WriteLine("[BBCiPlayer] Invalid ID in JSON");
                                continue;
                            }
                            var title = item.GetProperty("title").GetString();
                            if (title == null)
                            {
                                Console.WriteLine("[BBCiPlayer] Invalid title in JSON");
                                continue;
                            }

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
                                Source = "BBC iPlayer",
                                Subtitle = subtitle,
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
                // Parse ID for Season filtering (Format: brandId|seasonNumber)
                string brandId = id;
                string? targetSeason = null;

                if (id.Contains("|"))
                {
                    var parts = id.Split('|');
                    brandId = parts[0];
                    if (parts.Length > 1) targetSeason = parts[1];
                }

                // Fetch episodes for a brand/programme
                // Using per_page=100 to get a good chunk of episodes. Pagination not implemented for now.
                var url = $"https://ibl.api.bbci.co.uk/ibl/v1/programmes/{brandId}/episodes?per_page=100&rights=web&availability=available";
                Console.WriteLine($"[BBCiPlayer] Fetching episodes: {url}");

                var response = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);

                if (!doc.RootElement.TryGetProperty("programme_episodes", out var progEpisodes))
                {
                    // Try 'episodes' just in case
                    if (!doc.RootElement.TryGetProperty("episodes", out progEpisodes))
                    {
                        Console.WriteLine("[BBCiPlayer] 'programme_episodes' or 'episodes' property not found");
                        return Enumerable.Empty<MediaItem>();
                    }
                }

                JsonElement elements;
                if (progEpisodes.ValueKind == JsonValueKind.Array)
                {
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

                var allEpisodes = new List<(string Id, string Title, string Subtitle, string ImageUrl, string Synopsis, string Season)>();
                var seasons = new HashSet<string>();

                foreach (var item in elements.EnumerateArray())
                {
                    if (item.GetProperty("type").GetString() == "episode")
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

                        // Extract Season from Subtitle (e.g. "Series 1: Episode 1")
                        string season = "Unknown";
                        if (!string.IsNullOrEmpty(subtitle))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(subtitle, @"Series\s+(\d+)");
                            if (match.Success)
                            {
                                season = match.Groups[1].Value;
                            }
                        }
                        seasons.Add(season);

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

                        allEpisodes.Add((epId ?? "", title ?? "", subtitle, imageUrl, synopsis, season));
                    }
                }

                // 2. Logic for Return

                // Case A: Specific Season Requested
                if (targetSeason != null)
                {
                    return allEpisodes
                       .Where(x => x.Season == targetSeason)
                       .Select(x => CreateEpisodeItem(x, brandId));
                }

                // Case B: No specific season, but multiple seasons exist (ignoring "Unknown" if real seasons exist)
                var realSeasons = seasons.Where(s => s != "Unknown").OrderBy(s => s.Length).ThenBy(s => s).ToList(); // Simple sort, typically 1, 2, 10

                // If we have actual seasons found
                if (realSeasons.Count > 1)
                {
                    // Return Season Folders
                    var seasonItems = new List<MediaItem>();
                    foreach (var s in realSeasons)
                    {
                        // Find first episode to get an image?
                        var firstEp = allEpisodes.FirstOrDefault(x => x.Season == s);

                        seasonItems.Add(new MediaItem
                        {
                            Id = $"{brandId}|{s}",
                            Name = $"Series {s}",
                            Details = $"{allEpisodes.Count(x => x.Season == s)} Episodes",
                            ImageUrl = firstEp.ImageUrl,
                            Type = MediaType.Brand, // Treat seasons as Brands/Folders so they open a new view
                            Source = "BBC iPlayer",
                            Subtitle = "Season " + s,
                            Synopsis = "Season " + s,
                            IsLive = false,
                        });
                    }

                    // Also add "Unknown" season items if any, or just dump them? 
                    // Usually "Unknown" might just be specials or missed parsing. 
                    // Let's add an "Other" folder if there are Unknowns? Or just append them?
                    // For now, let's just stick to the main seasons.

                    return seasonItems;
                }

                // Case C: Only 1 season or no season info -> Return all episodes
                return allEpisodes.Select(x => CreateEpisodeItem(x, brandId));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BBCiPlayer] Failed to get children for {id}: {ex.Message}");
                return Enumerable.Empty<MediaItem>();
            }
        }

        private MediaItem CreateEpisodeItem((string Id, string Title, string Subtitle, string ImageUrl, string Synopsis, string Season) x, string brandId)
        {
            return new MediaItem
            {
                Id = x.Id,
                Name = x.Title,
                Details = x.Subtitle,
                Subtitle = x.Subtitle,
                Synopsis = x.Synopsis,
                ImageUrl = x.ImageUrl,
                IsLive = false,
                Type = MediaType.Video,
                StreamUrl = $"https://www.bbc.co.uk/iplayer/episode/{x.Id}",
                Source = "BBC iPlayer"
            };
        }
    }
}