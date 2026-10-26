using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Baird.Controls;
using Baird.Models;
using Baird.Services;
using Baird.Tests.Mocks;
using Baird.ViewModels;
using SkiaSharp;
using Xunit;

namespace Baird.Tests
{
    /// <summary>
    /// Renders every non-spike page with canned data to PNGs for the design review. Skipped unless
    /// DESIGN_SHOTS_DIR is set, so it does nothing in a normal test run.
    /// </summary>
    public class DesignScreenshots
    {
        private static readonly string? OutDir = Environment.GetEnvironmentVariable("DESIGN_SHOTS_DIR");

        // ---- canned data -------------------------------------------------------------------------------

        private static string _posterDir = "";

        private static string Poster(string title, uint hue)
        {
            var path = Path.Combine(_posterDir, $"{Math.Abs(title.GetHashCode())}.png");
            if (File.Exists(path)) return path;
            using var bmp = new SKBitmap(320, 180);
            using var canvas = new SKCanvas(bmp);
            var c1 = SKColor.FromHsl(hue, 55, 38);
            var c2 = SKColor.FromHsl((hue + 40) % 360, 60, 18);
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(320, 180), new[] { c1, c2 }, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            canvas.DrawRect(0, 0, 320, 180, paint);
            using var text = new SKPaint { Color = SKColors.White.WithAlpha(230), TextSize = 26, IsAntialias = true, FakeBoldText = true };
            canvas.DrawText(title.Length > 22 ? title[..22] : title, 16, 100, text);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(path, data.ToArray());
            return path;
        }

        private static MediaItemData Chan(string num, string name, uint hue) => new()
        {
            Id = "ch" + num,
            Name = name,
            Details = "",
            ImageUrl = Poster(name, hue),
            IsLive = true,
            StreamUrl = "http://x/" + num,
            Source = "TvHeadend",
            ChannelNumber = num,
            Type = MediaType.Channel,
            Synopsis = "",
            Subtitle = ""
        };

        private static MediaItemData Vod(string id, string name, string source, string year, uint hue, int mins = 0, MediaType type = MediaType.Video, string synopsis = "") => new()
        {
            Id = id,
            Name = name,
            Details = year,
            ImageUrl = Poster(name, hue),
            IsLive = false,
            StreamUrl = "http://x/" + id,
            Source = source,
            Type = type,
            Synopsis = synopsis,
            Subtitle = year,
            Duration = TimeSpan.FromMinutes(mins)
        };

        private static readonly MediaItemData[] Channels =
        {
            Chan("1", "BBC One HD", 200), Chan("2", "BBC Two HD", 150), Chan("3", "ITV1 HD", 20), Chan("4", "Channel 4 HD", 280),
            Chan("5", "Channel 5 HD", 330), Chan("9", "BBC Four HD", 90), Chan("80", "BBC News Channel", 0), Chan("300", "Film4", 40),
        };

        private static readonly MediaItemData[] Streaming =
        {
            Vod("jf1", "28 Years Later", "Jellyfin: jellyfin.example.io", "2025", 0, 115, synopsis: "A group of survivors venture from their quarantined island into the mainland."),
            Vod("jf2", "Back to the Future", "Jellyfin: jellyfin.example.io", "1985", 30, 116),
            Vod("jf3", "Blade Runner 2049", "Jellyfin: jellyfin.example.io", "2017", 190, 164),
            Vod("jf4", "Paddington 2", "Jellyfin: jellyfin.example.io", "2017", 45, 103),
            Vod("bb1", "Doctor Who", "BBC iPlayer", "2005-", 230, 0, MediaType.Brand, "The Doctor and friends travel through time and space."),
            Vod("bb2", "Planet Earth III", "BBC iPlayer", "2023", 120, 58),
            Vod("nf1", "Stranger Things", "Netflix", "2016-", 350, 0, MediaType.Brand, "A missing boy and a girl with powers."),
            Vod("nf2", "The Crown", "Netflix", "2016-", 260, 0, MediaType.Brand),
            Vod("yt1", "How Analogue TV Worked", "YouTube", "2024", 70, 14),
        };

        private static List<EpgProgram> Schedule(MediaItemData ch, int seed)
        {
            string[][] shows =
            {
                new[] { "BBC News at Six", "The weather and the latest headlines." },
                new[] { "The One Show", "Magazine show with topical stories and guests." },
                new[] { "Question Time", "Topical debate from a different town each week." },
                new[] { "Match of the Day", "Highlights from the weekend's Premier League football." },
                new[] { "Newsnight", "In-depth analysis of the day's events." },
                new[] { "Gardeners' World", "Practical advice for the season ahead." },
                new[] { "Antiques Roadshow", "Experts value treasures brought along by the public." },
            };
            var start = DateTimeOffset.UtcNow.AddMinutes(-25).ToUnixTimeSeconds();
            var list = new List<EpgProgram>();
            for (int i = 0; i < 6; i++)
            {
                var s = shows[(seed + i) % shows.Length];
                var len = (i % 2 == 0 ? 30 : 60) * 60;
                list.Add(new EpgProgram
                {
                    Id = $"{ch.Id}-{i}",
                    ChannelId = ch.Id,
                    ChannelName = ch.Name,
                    ChannelNumber = ch.ChannelNumber,
                    Title = s[0],
                    Subtitle = "Episode " + (i + 3),
                    Description = s[1],
                    StartUnix = start,
                    StopUnix = start + len
                });
                start += len;
            }
            return list;
        }

        private class Provider : IMediaProvider
        {
            public string Name => "Demo";
            public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult(Channels.Concat(Streaming).AsEnumerable());
            public Task<IEnumerable<MediaItemData>> SearchAsync(string query, CancellationToken cancellationToken = default) =>
                Task.FromResult(Channels.Concat(Streaming).Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
            public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id)
            {
                if (id != "bb1" && id != "nf1" && id != "nf2") return Task.FromResult(Enumerable.Empty<MediaItemData>());
                var eps = Enumerable.Range(1, 8).Select(n => Vod($"{id}-e{n}", $"S01E0{n}  Episode title {n}", "BBC iPlayer", "2024", (uint)(n * 30), 45 + n,
                    synopsis: "A short description of this episode.")).ToList();
                return Task.FromResult<IEnumerable<MediaItemData>>(eps);
            }
            public Task<MediaItemData?> GetItemAsync(string id) =>
                Task.FromResult(Channels.Concat(Streaming).FirstOrDefault(i => i.Id == id));
        }

        private class History : IHistoryService
        {
            private readonly List<HistoryItem> _items = new()
            {
                new HistoryItem { Id = "ch80", LastWatched = DateTime.Now.AddMinutes(-10) },
                new HistoryItem { Id = "jf1", LastPosition = TimeSpan.FromMinutes(70), Duration = TimeSpan.FromMinutes(115), LastWatched = DateTime.Now.AddHours(-3) },
                new HistoryItem { Id = "jf2", LastPosition = TimeSpan.FromMinutes(12), Duration = TimeSpan.FromMinutes(116), LastWatched = DateTime.Now.AddDays(-1) },
                new HistoryItem { Id = "bb2", LastPosition = TimeSpan.FromMinutes(40), Duration = TimeSpan.FromMinutes(58), LastWatched = DateTime.Now.AddDays(-2) },
                new HistoryItem { Id = "ch300", LastWatched = DateTime.Now.AddDays(-3) },
                new HistoryItem { Id = "jf3", LastPosition = TimeSpan.FromMinutes(164), Duration = TimeSpan.FromMinutes(164), IsFinished = true, LastWatched = DateTime.Now.AddDays(-9) },
            };
            public Task UpsertAsync(MediaItemViewModel media, TimeSpan position, TimeSpan duration) => Task.CompletedTask;
            public Task<List<HistoryItem>> GetHistoryAsync() => Task.FromResult(_items);
            public HistoryItem? GetProgress(string id) => _items.FirstOrDefault(i => i.Id == id);
        }

        private class Watchlist : IWatchlistService
        {
            public event EventHandler? WatchlistUpdated;
            public Task AddAsync(string id) => Task.CompletedTask;
            public Task RemoveAsync(string id) => Task.CompletedTask;
            public Task<HashSet<string>> GetWatchlistIdsAsync() => Task.FromResult(new HashSet<string> { "jf3", "jf4", "bb1", "nf1", "nf2" });
            public bool IsOnWatchlist(string id) => false;
        }

        private class SearchHistory : ISearchHistoryService
        {
            public Task AddSearchTermAsync(string term) => Task.CompletedTask;
            public Task<IEnumerable<string>> GetSuggestedTermsAsync(int maxCount) =>
                Task.FromResult<IEnumerable<string>>(new[] { "doctor who", "blade runner", "bbc news", "paddington" });
        }

        private class Seerr : IJellyseerrService
        {
            private static JellyseerrSearchResult R(int id, string t, string type, int status, string date, double vote) => new()
            { Id = id, Title = t, MediaType = type, MediaInfoStatus = status, ReleaseDate = date, VoteAverage = vote, Overview = "Overview text." };
            public Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string query, int page, CancellationToken cancellationToken = default) =>
                GetTrendingAsync(page, cancellationToken);
            public Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int page, CancellationToken cancellationToken = default) =>
                Task.FromResult<IEnumerable<JellyseerrSearchResult>>(new[]
                {
                    R(1, "Dune: Part Three", "movie", 1, "2026-12-18", 8.1), R(2, "The Last of Us", "tv", 5, "2023-01-15", 8.7),
                    R(3, "Severance", "tv", 2, "2022-02-18", 8.7), R(4, "Sinners", "movie", 5, "2025-04-18", 7.6),
                    R(5, "Andor", "tv", 4, "2022-09-21", 8.4), R(6, "Alien: Earth", "tv", 1, "2025-08-12", 7.7),
                    R(7, "Weapons", "movie", 3, "2025-08-08", 7.3), R(8, "Slow Horses", "tv", 5, "2022-04-01", 8.0),
                });
            public Task<JellyseerrRequestResponse> CreateRequestAsync(int mediaId, string mediaType, CancellationToken cancellationToken = default) =>
                Task.FromResult(new JellyseerrRequestResponse { Success = true });
            public Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(CancellationToken cancellationToken = default)
            {
                JellyseerrRequest Q(int id, string t, string type, int status, int mi, double? dl) => new()
                {
                    Id = id,
                    Title = t,
                    MediaType = type,
                    Status = status,
                    MediaInfoStatus = mi,
                    DownloadProgress = dl,
                    CreatedAt = DateTime.UtcNow.AddDays(-id).ToString("o"),
                    UpdatedAt = DateTime.UtcNow.AddHours(-id).ToString("o")
                };
                return Task.FromResult<IEnumerable<JellyseerrRequest>>(new[]
                {
                    Q(1, "Severance", "tv", 2, 3, 62), Q(2, "Dune: Part Three", "movie", 1, 2, null), Q(3, "Andor", "tv", 2, 4, 100),
                    Q(4, "The Last of Us", "tv", 2, 5, 100), Q(5, "Weapons", "movie", 3, 1, null),
                });
            }
        }

        // ---- harness -----------------------------------------------------------------------------------

        private static async Task Settle(int ms = 1200)
        {
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(25);
            }
            Dispatcher.UIThread.RunJobs();
        }

        private static void Shot(Window w, string name)
        {
            Dispatcher.UIThread.RunJobs();
            using var frame = w.CaptureRenderedFrame();
            frame!.Save(Path.Combine(OutDir!, name + ".png"));
        }

        [AvaloniaFact]
        public async Task CaptureAllPages()
        {
            if (string.IsNullOrEmpty(OutDir)) return;
            Directory.CreateDirectory(OutDir);
            _posterDir = Path.Combine(Path.GetTempPath(), "baird-design-posters");
            Directory.CreateDirectory(_posterDir);

            var epg = new MockEpgService();
            foreach (var (c, i) in Channels.Select((c, i) => (c, i))) epg.MockSchedules[c.Id] = Schedule(c, i);

            var data = new DataService(new IMediaProvider[] { new Provider() }, new History(), new Watchlist(), new MediaItemCache(), new MockMediaDataCache());
            SearchTiming.Debounce = TimeSpan.FromMilliseconds(100);
            var vm = new MainViewModel(null!, data, new SearchHistory(), new ScreensaverService(), new MockCecService(), new Seerr(), epg);
            await vm.RefreshChannels();
            await vm.History.RefreshAsync();
            await vm.Watchlist.RefreshAsync();
            await vm.Epg.RefreshAsync();
            await Settle(500);

            // ---- tabbed pages: the real tab bar + page, 1920x1080 ----
            foreach (var spike in vm.MainMenu.Tabs.Where(t => t.Title.Contains("Spike") || t.Title is "Netflix Search" or "Disney Search").ToList())
                vm.MainMenu.Tabs.Remove(spike);
            var tabs = new TabNavigationControl { DataContext = vm.MainMenu };
            var window = new Window { Width = 1920, Height = 1080, Background = Brushes.Black, Content = tabs };
            window.Show();
            await Settle();

            var wanted = new (string Title, string File)[]
            {
                ("History", "01-history"), ("Guide", "02-guide"), ("Search", "03-search-idle"), ("Watchlist", "05-watchlist"),
                ("Settings", "08-settings"),
                ("CEC Debug", "09-cec-debug"), ("A/V Debug", "10-av-debug"),
            };
            foreach (var (title, file) in wanted)
            {
                var idx = vm.MainMenu.Tabs.ToList().FindIndex(t => t.Title == title);
                vm.MainMenu.SelectedIndex = idx;
                await Settle(title is "Discover" or "Requests" or "Guide" or "History" or "Watchlist" ? 2500 : 1200);
                Shot(window, file);

                if (title == "Search")
                {
                    vm.OmniSearch.SearchText = "do";
                    await Settle(2500);
                    Shot(window, "04-search-results");
                }
            }

            // ---- Discover / Requests: TabNavigationControl has no DataTemplate for these two view models (they show the class
            // name inside the tab bar), so render the controls on their own ----
            window.Content = new SeerrchControl { DataContext = vm.Seerrch };
            await Settle(3000);
            Shot(window, "06-discover");
            window.Content = new RequestsControl { DataContext = vm.Requests };
            await Settle(3000);
            Shot(window, "07-requests");

            // ---- programme detail (a pushed page, no tab bar) ----
            var show = (await data.GetListingAsync()).First(i => i.Id == "bb1");
            var detail = new ProgrammeDetailControl { DataContext = new ProgrammeDetailViewModel(data, show) };
            window.Content = detail;
            await Settle(2500);
            Shot(window, "11-programme-detail");

            // ---- guide overlay shown over live TV ----
            vm.ActiveItem = vm.AllChannels.First(c => c.Id == "ch1");
            vm.CurrentProgrammeName = "BBC News at Six";
            vm.IsEpgActive = true;
            var overlayHost = new Grid
            {
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#1d2b3a"), 0), new GradientStop(Color.Parse("#0a0f14"), 1) }
                }
            };
            overlayHost.Children.Add(new EpgOverlayControl { DataContext = vm, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, Height = 280 });
            window.Content = overlayHost;
            await Settle(2500);
            Shot(window, "12-guide-overlay");

            window.Close();
        }
    }
}
