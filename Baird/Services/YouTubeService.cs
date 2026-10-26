using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    /// <summary>Runs yt-dlp with these arguments and returns the lines it printed (one JSON object per line). Replaced in tests.</summary>
    public delegate Task<IReadOnlyList<string>> YtDlpRunner(IReadOnlyList<string> args, CancellationToken ct);

    /// <summary>
    /// YouTube in Baird's search: videos, and channels treated as series. A channel (<c>yt:channel/{UC id}</c>) opens to its
    /// playlists as seasons plus "All videos" (newest first, no Shorts or live streams), or straight to its videos when it has no
    /// playlists. Videos load a page at a time: a folder id ending <c>@2</c> is that folder's second page of <see cref="PageSize"/>.
    /// </summary>
    public class YouTubeService : IMediaProvider
    {
        public const string SourceName = "YouTube";
        public const string IdPrefix = "yt:";
        public const string ChannelPrefix = IdPrefix + "channel/";
        public const int PageSize = 50;
        private const int MaxPlaylists = 20;
        private const string ChannelSearchFilter = "EgIQAg%253D%253D";
        private static readonly Regex SafeId = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
        // Playlists YouTube makes or creators leave as defaults: not a season of anything.
        private static readonly string[] NotSeasons = { "favorites", "favourites", "liked videos", "popular uploads", "popular videos", "watch later" };

        private readonly YtDlpRunner _run;

        public YouTubeService(YtDlpRunner? run = null) { _run = run ?? RunYtDlpAsync; }

        public string Name => SourceName;

        public static bool IsChannelId(string id) => id.StartsWith(IdPrefix, StringComparison.Ordinal);

        /// <summary>The id of the next page of a folder (<c>yt:channel/UC|videos</c> → <c>…@2</c>), or null when this page was the last (short).</summary>
        public static string? NextPageId(string folderId, int returned)
        {
            if (returned < PageSize || !IsChannelId(folderId)) return null;
            ParseId(folderId, out var baseId, out var page);
            return $"{baseId}@{page + 1}";
        }

        private static void ParseId(string id, out string baseId, out int page)
        {
            baseId = id; page = 1;
            var at = id.LastIndexOf('@');
            if (at > 0 && int.TryParse(id[(at + 1)..], out var p) && p >= 1) { baseId = id[..at]; page = p; }
        }
        public Task<MediaItemData?> GetItemAsync(string id)
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

        public Task<IEnumerable<MediaItemData>> GetListingAsync()
        {
            // Browsing YouTube without a query is not supported in this simple implementation
            return Task.FromResult(Enumerable.Empty<MediaItemData>());
        }

        public async Task<IEnumerable<MediaItemData>> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return Enumerable.Empty<MediaItemData>();

            try
            {
                // "--" stops option parsing before the positional arg; ArgumentList means quotes in the query can't inject options.
                var videos = RunAsync(new[] { "--dump-json", "--flat-playlist", "--", $"ytsearch5:{query}" }, cancellationToken);
                var channels = RunAsync(new[] { "--dump-json", "--flat-playlist", "--playlist-end", "3", "--",
                    $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}&sp={ChannelSearchFilter}" }, cancellationToken);
                await Task.WhenAll(videos, channels);

                // Channels first: a search for a channel's name should offer the channel before its videos.
                return channels.Result.Select(ParseChannel).Concat(videos.Result.Select(ParseVideo)).Where(i => i != null).Select(i => i!).ToList();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[YouTubeService] YouTube search failed: {ex.Message}");
                return Enumerable.Empty<MediaItemData>();
            }
        }

        private async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            try { return await _run(args, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"[YouTubeService] yt-dlp failed: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private static async Task<IReadOnlyList<string>> RunYtDlpAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            var startInfo = new ProcessStartInfo { FileName = "yt-dlp", RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) startInfo.ArgumentList.Add(a);
            using var process = Process.Start(startInfo);
            if (process == null) return Array.Empty<string>();
            using var _ = ct.Register(() => { try { process.Kill(); } catch { } });
            var lines = new List<string>();
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) != null) lines.Add(line);
            ct.ThrowIfCancellationRequested();
            return lines;
        }

        public static MediaItemData? ParseVideo(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = Str(root, "id");
                var title = Str(root, "title");
                if (id == null || title == null)
                {
                    Console.WriteLine($"[YouTubeService] Invalid entry in yt-dlp output: {line}");
                    return null;
                }
                // An upcoming premiere or a live stream is not an episode you can watch.
                if (Str(root, "live_status") is "is_upcoming" or "is_live") return null;

                var duration = TimeSpan.Zero;
                if (root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number) duration = TimeSpan.FromSeconds(d.GetDouble());

                return new MediaItemData
                {
                    Id = id,
                    Name = title,
                    Details = Str(root, "uploader") ?? Str(root, "channel") ?? "",
                    ImageUrl = LastThumbnail(root) ?? $"https://i.ytimg.com/vi/{id}/hqdefault.jpg",
                    IsLive = false,
                    StreamUrl = Str(root, "webpage_url") ?? Str(root, "url") ?? GetStreamUrl(id),
                    Source = SourceName,
                    Type = MediaType.Video,
                    Synopsis = "",
                    Subtitle = "",
                    Duration = duration,
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[YouTubeService] Error parsing yt-dlp line: {ex.Message}");
                return null;
            }
        }

        /// <summary>A channel from the channel-filtered search page; null for anything else on that page.</summary>
        public static MediaItemData? ParseChannel(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = Str(root, "channel_id") ?? Str(root, "id");
                var title = Str(root, "title") ?? Str(root, "channel");
                if (id == null || title == null || !id.StartsWith("UC", StringComparison.Ordinal) || !SafeId.IsMatch(id)) return null;

                var details = root.TryGetProperty("channel_follower_count", out var f) && f.ValueKind == JsonValueKind.Number
                    ? $"{Subscribers(f.GetInt64())} subscribers" : "YouTube channel";
                return new MediaItemData
                {
                    Id = ChannelPrefix + id,
                    Name = title,
                    Details = details,
                    ImageUrl = LastThumbnail(root) ?? "",
                    IsLive = false,
                    StreamUrl = "",
                    Source = SourceName,
                    Type = MediaType.Brand,
                    Synopsis = Str(root, "description") ?? "",
                    Subtitle = "",
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[YouTubeService] Error parsing yt-dlp channel: {ex.Message}");
                return null;
            }
        }

        private static string Subscribers(long n) => n switch
        {
            >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
            >= 1_000 => $"{n / 1_000.0:0.#}K",
            _ => n.ToString(),
        };

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : null;

        private static string? LastThumbnail(JsonElement root)
        {
            if (!root.TryGetProperty("thumbnails", out var t) || t.ValueKind != JsonValueKind.Array || t.GetArrayLength() == 0) return null;
            var url = Str(t[t.GetArrayLength() - 1], "url");
            return url == null ? null : url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;
        }

        /// <summary>
        /// A channel's seasons: <c>yt:channel/{id}</c> gives "All videos" plus its playlists (or the newest videos straight away when it
        /// has no playlists); <c>…|videos</c> and <c>…|list/{playlist}</c> give that folder's videos, a page at a time (<c>@2</c>…).
        /// Answers only for its own ids; a yt-dlp failure gives an empty list.
        /// </summary>
        public async Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id)
        {
            if (!id.StartsWith(ChannelPrefix, StringComparison.Ordinal)) return Enumerable.Empty<MediaItemData>();

            ParseId(id, out var baseId, out var page);
            var rest = baseId[ChannelPrefix.Length..];
            var bar = rest.IndexOf('|');
            var channel = bar < 0 ? rest : rest[..bar];
            var folder = bar < 0 ? null : rest[(bar + 1)..];
            if (!SafeId.IsMatch(channel)) return Enumerable.Empty<MediaItemData>();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var ct = timeout.Token;

            // A bare channel's later pages are its videos: the playlists-as-seasons choice is only made on page 1.
            if (folder == null && page > 1) return await VideosAsync($"https://www.youtube.com/channel/{channel}/videos", page, ct);
            if (folder == null)
            {
                var lists = (await RunAsync(new[] { "--dump-json", "--flat-playlist", "--playlist-end", MaxPlaylists.ToString(), "--",
                    $"https://www.youtube.com/channel/{channel}/playlists" }, ct)).Select(ParsePlaylist).Where(p => p != null).Select(p => p!).ToList();
                if (lists.Count == 0) return await VideosAsync($"https://www.youtube.com/channel/{channel}/videos", 1, ct);

                var image = lists[0].ImageUrl;
                var folders = new List<MediaItemData> { Folder($"{ChannelPrefix}{channel}|videos", "All videos", "Newest first", image) };
                folders.AddRange(lists.Select(p => Folder($"{ChannelPrefix}{channel}|list/{p.Id}", p.Name, p.Details, p.ImageUrl)));
                return folders;
            }
            if (folder == "videos") return await VideosAsync($"https://www.youtube.com/channel/{channel}/videos", page, ct);
            if (folder.StartsWith("list/", StringComparison.Ordinal) && SafeId.IsMatch(folder[5..]))
                return await VideosAsync($"https://www.youtube.com/playlist?list={folder[5..]}", page, ct);
            return Enumerable.Empty<MediaItemData>();
        }

        private async Task<IEnumerable<MediaItemData>> VideosAsync(string url, int page, CancellationToken ct)
        {
            var from = (page - 1) * PageSize + 1;
            var lines = await RunAsync(new[] { "--dump-json", "--flat-playlist", "--playlist-items", $"{from}:{from + PageSize - 1}", "--", url }, ct);
            return lines.Select(ParseVideo).Where(v => v != null).Select(v => v!).ToList();
        }

        private static MediaItemData? ParsePlaylist(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = Str(root, "id");
                var title = Str(root, "title");
                if (id == null || title == null || !SafeId.IsMatch(id)) return null;
                if (NotSeasons.Contains(title.Trim(), StringComparer.OrdinalIgnoreCase)) return null;
                return new MediaItemData { Id = id, Name = title, Details = "Playlist", ImageUrl = LastThumbnail(root) ?? "", IsLive = false, StreamUrl = "", Source = SourceName, Type = MediaType.Brand, Synopsis = "", Subtitle = "" };
            }
            catch { return null; }
        }

        private static MediaItemData Folder(string id, string name, string details, string image) => new()
        {
            Id = id, Name = name, Details = details, ImageUrl = image, IsLive = false, StreamUrl = "", Source = SourceName,
            Type = MediaType.Brand, Synopsis = name, Subtitle = name,
        };

        private static string GetStreamUrl(string itemId)
        {
            // mpv handles youtube URLs automatically via ytdl hook
            return $"https://www.youtube.com/watch?v={itemId}";
        }
    }
}
