using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Baird.Services
{
    public class YouTubeService : IMediaProvider
    {
        public Task<MediaItem?> GetItemAsync(string id)
        {
            // For now, return null as we don't have a direct lookup without search
            // Or we could run yt-dlp on the ID?
            // "https://www.youtube.com/watch?v={id}"
            // Let's implement a quick lookup
            return Task.Run(async () =>
            {
                var items = await SearchAsync(id);
                return items.FirstOrDefault(i => i.Id == id);
            });
        }

        public Task<IEnumerable<MediaItem>> GetListingAsync()
        {
            // Browsing YouTube without a query is not supported in this simple implementation
            return Task.FromResult(Enumerable.Empty<MediaItem>());
        }

        public async Task<IEnumerable<MediaItem>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<MediaItem>();

            try
            {
                // Use yt-dlp to search for the query and get JSON metadata
                // --dump-json: output metadata as JSON
                // --flat-playlist: don't extract information for each video in the result (faster for search)
                // ytsearch5: search for the first 5 results
                var startInfo = new ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = $"--dump-json --flat-playlist \"ytsearch5:{query}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null) return Enumerable.Empty<MediaItem>();

                var items = new List<MediaItem>();
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        var id = root.GetProperty("id").GetString();
                        if (id == null)
                        {
                            Console.WriteLine($"Invalid ID in yt-dlp output: {line}");
                            continue;
                        }
                        var title = root.GetProperty("title").GetString();
                        if (title == null)
                        {
                            Console.WriteLine($"Invalid title in yt-dlp output: {line}");
                            continue;
                        }

                        var uploader = root.GetProperty("uploader").GetString() ?? "";
                        var url = root.GetProperty("webpage_url").GetString() ?? GetStreamUrlInternal(id);

                        string imageUrl = "";
                        if (doc.RootElement.TryGetProperty("thumbnails", out var thumbnails) && thumbnails.GetArrayLength() > 0)
                        {
                            imageUrl = thumbnails[thumbnails.GetArrayLength() - 1].GetProperty("url").GetString() ?? "";
                        }
                        else
                        {
                            imageUrl = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg"; // Fallback to default YouTube thumbnail
                        }

                        // Extract duration (in seconds, may be null for live streams)
                        TimeSpan duration = TimeSpan.Zero;
                        if (doc.RootElement.TryGetProperty("duration", out var durationProp))
                        {
                            if (durationProp.ValueKind == JsonValueKind.Number)
                            {
                                var durationSeconds = durationProp.GetDouble();
                                duration = TimeSpan.FromSeconds(durationSeconds);
                            }
                        }

                        items.Add(new MediaItem
                        {
                            Id = id,
                            Name = title,
                            Details = uploader,
                            ImageUrl = imageUrl,
                            IsLive = false, // TODO: could check if 'is_live' in json, but simplified for now. Youtube live streams are not the same as channels.
                            StreamUrl = url,
                            Source = "YouTube",
                            Type = MediaType.Video,
                            Synopsis = "",
                            Subtitle = "",
                            Duration = duration,
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error parsing yt-dlp line: {ex.Message}");
                    }
                }

                return items;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"YouTube search failed: {ex.Message}");
                return Enumerable.Empty<MediaItem>();
            }
        }

        public Task<IEnumerable<MediaItem>> GetChildrenAsync(string id)
        {
            return Task.FromResult(Enumerable.Empty<MediaItem>());
        }

        private string GetStreamUrlInternal(string itemId)
        {
            // mpv handles youtube URLs automatically via ytdl hook
            return $"https://www.youtube.com/watch?v={itemId}";
        }
    }
}
