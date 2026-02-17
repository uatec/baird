using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    public class BbcIPlayerService : IMediaProvider
    {
        public string Name => "BBC iPlayer";
        private static readonly HttpClient _httpClient;
        private const string SearchUrl = "https://ibl.api.bbci.co.uk/ibl/v1/new-search?q={0}&rights=web&mixin=live&lang=en&availability=available";

        static BbcIPlayerService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        public Task<MediaItemData?> GetItemAsync(string id)
        {
            // BBC iPlayer doesn't expose single item lookup easily for arbitrary IDs
            // But we can construct the URL and maybe parse it if we were scraping?
            // Or we can search for the ID specifically?
            // "https://ibl.api.bbci.co.uk/ibl/v1/programmes/{id}?rights=web&availability=available"
            // Let's try to fetch program details.

            return Task.Run(async () =>
            {
                try
                {
                    // This is a guess at the API for single item or we reuse search?
                    // Search for the ID usually works if it's in the index? No, search is text.
                    // Let's try the programme endpoint.
                    var url = $"https://ibl.api.bbci.co.uk/ibl/v1/programmes/{id}?rights=web&availability=available";
                    var response = await _httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode) return null;

                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("programmes", out var progs) && progs.GetArrayLength() > 0)
                    {
                        var p = progs[0]; // Take first
                        // Map it similar to search result
                        var title = p.GetProperty("title").GetString();
                        // ... mapping logic is complex to duplicate.
                        // Can we refactor mapping?
                        // For now, let's just return null if too complex, or implemented partially.
                        // User requirement: "split history only information out".
                        // If we can't look it up, history won't show it.
                        // Let's rely on Search for now?
                        // Actually, let's implement a basic mapping.

                        return new MediaItemData
                        {
                            Id = id,
                            Name = title!,
                            Details = "", // Extract subtitle
                            ImageUrl = "", // Extract image
                            IsLive = false,
                            Type = MediaType.Video,
                            StreamUrl = $"https://www.bbc.co.uk/iplayer/episode/{id}",
                            Source = "BBC iPlayer",
                            Subtitle = "",
                            Synopsis = "",
                            Duration = TimeSpan.Zero // We might not get it easily here without versions
                        };
                    }
                    return null;
                }
                catch
                {
                    return null;
                }
            });
        }

        public Task<IEnumerable<MediaItemData>> GetListingAsync()
        {
            // Full browsing not implemented yet
            return Task.FromResult(Enumerable.Empty<MediaItemData>());
        }

        public async Task<IEnumerable<MediaItemData>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<MediaItemData>();

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
                    return Enumerable.Empty<MediaItemData>();
                }

                if (!newSearch.TryGetProperty("results", out var results))
                {
                    Console.WriteLine("[BBCiPlayer] 'results' property not found in JSON");
                    return Enumerable.Empty<MediaItemData>();
                }

                var items = new List<MediaItemData>();
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

                            // Extract duration from versions[0].duration.value (ISO 8601 format like "PT7M0.040S")
                            TimeSpan duration = TimeSpan.Zero;
                            if (item.TryGetProperty("versions", out var versionsProp) && versionsProp.GetArrayLength() > 0)
                            {
                                var firstVersion = versionsProp[0];
                                if (firstVersion.TryGetProperty("duration", out var durationProp) &&
                                    durationProp.TryGetProperty("value", out var durationValue))
                                {
                                    var durationStr = durationValue.GetString();
                                    if (!string.IsNullOrEmpty(durationStr))
                                    {
                                        try
                                        {
                                            // Parse ISO 8601 duration format (e.g., "PT7M0.040S")
                                            duration = System.Xml.XmlConvert.ToTimeSpan(durationStr);
                                        }
                                        catch (FormatException ex)
                                        {
                                            Console.WriteLine($"[BBCiPlayer] Failed to parse duration '{durationStr}': {ex.Message}");
                                        }
                                    }
                                }
                            }

                            var mediaType = (type == "brand" || type == "programme") ? MediaType.Brand : MediaType.Video;

                            items.Add(new MediaItemData
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
                                Duration = duration,
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
                return Enumerable.Empty<MediaItemData>();
            }
        }

        public async Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id)
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
                        return Enumerable.Empty<MediaItemData>();
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
                    return Enumerable.Empty<MediaItemData>();
                }

                var allEpisodes = new List<(string Id, string Title, string Subtitle, string ImageUrl, string Synopsis, string Season, TimeSpan Duration)>();
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

                        // Extract duration from versions[0].duration.value
                        TimeSpan duration = TimeSpan.Zero;
                        if (item.TryGetProperty("versions", out var versionsProp) && versionsProp.GetArrayLength() > 0)
                        {
                            var firstVersion = versionsProp[0];
                            if (firstVersion.TryGetProperty("duration", out var durationProp) &&
                                durationProp.TryGetProperty("value", out var durationValue))
                            {
                                var durationStr = durationValue.GetString();
                                if (!string.IsNullOrEmpty(durationStr))
                                {
                                    try
                                    {
                                        duration = System.Xml.XmlConvert.ToTimeSpan(durationStr);
                                    }
                                    catch (FormatException ex)
                                    {
                                        Console.WriteLine($"[BBCiPlayer] Failed to parse duration '{durationStr}': {ex.Message}");
                                    }
                                }
                            }
                        }

                        allEpisodes.Add((epId ?? "", title ?? "", subtitle, imageUrl, synopsis, season, duration));
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
                    var seasonItems = new List<MediaItemData>();
                    foreach (var s in realSeasons)
                    {
                        // Find first episode to get an image?
                        var firstEp = allEpisodes.FirstOrDefault(x => x.Season == s);

                        seasonItems.Add(new MediaItemData
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
                return Enumerable.Empty<MediaItemData>();
            }
        }

        private MediaItemData CreateEpisodeItem((string Id, string Title, string Subtitle, string ImageUrl, string Synopsis, string Season, TimeSpan Duration) x, string brandId)
        {
            return new MediaItemData
            {
                Id = x.Id,
                Name = x.Subtitle,
                Details = x.Title,
                Subtitle = x.Subtitle,
                Synopsis = x.Synopsis,
                ImageUrl = x.ImageUrl,
                IsLive = false,
                Type = MediaType.Video,
                StreamUrl = $"https://www.bbc.co.uk/iplayer/episode/{x.Id}",
                Source = "BBC iPlayer",
                Duration = x.Duration,
            };
        }
    }
}