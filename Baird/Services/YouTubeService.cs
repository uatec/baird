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
                string line;
                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        var id = root.GetProperty("id").GetString();
                        var title = root.GetProperty("title").GetString();
                        var uploader = root.TryGetProperty("uploader", out var u) ? u.GetString() : "YouTube";

                        items.Add(new MediaItem
                        {
                            Id = id,
                            Name = title,
                            Details = uploader,
                            ImageUrl = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg",
                            IsLive = false, // Note: could check if 'is_live' in json, but simplified for now
                            StreamUrl = GetStreamUrlInternal(id),
                            Source = "YouTube",
                            Type = MediaType.Video,
                            Synopsis = "",
                            Subtitle = ""
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
