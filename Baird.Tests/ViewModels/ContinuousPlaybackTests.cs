using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baird.Models;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class ContinuousPlaybackTests
    {
        private class TestMediaProvider : IMediaProvider
        {
            public string Name => "Test Provider";
            private readonly Dictionary<string, List<MediaItemData>> _childrenMap = new();

            public void SetChildren(string parentId, List<MediaItemData> children)
            {
                _childrenMap[parentId] = children;
            }

            public Task InitializeAsync() => Task.CompletedTask;
            public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult(Enumerable.Empty<MediaItemData>());
            public Task<IEnumerable<MediaItemData>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default)
                => Task.FromResult(Enumerable.Empty<MediaItemData>());

            public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id)
            {
                if (_childrenMap.TryGetValue(id, out var children))
                {
                    return Task.FromResult((IEnumerable<MediaItemData>)children);
                }
                return Task.FromResult(Enumerable.Empty<MediaItemData>());
            }

            public Task<MediaItemData?> GetItemAsync(string id) => Task.FromResult<MediaItemData?>(null);
        }

        private class TestHistoryService : IHistoryService
        {
            private readonly Dictionary<string, HistoryItem> _history = new();

            public Task<List<HistoryItem>> GetHistoryAsync() => Task.FromResult(_history.Values.ToList());

            public Task UpsertAsync(MediaItem item, TimeSpan position, TimeSpan duration)
            {
                _history[item.Id] = new HistoryItem
                {
                    Id = item.Id,
                    LastPosition = position,
                    Duration = duration,
                    IsFinished = false,
                    LastWatched = DateTime.Now
                };
                return Task.CompletedTask;
            }

            public HistoryItem? GetProgress(string id)
            {
                return _history.TryGetValue(id, out var item) ? item : null;
            }

            public Task ClearHistoryAsync() => Task.CompletedTask;
        }

        private class TestSearchHistoryService : ISearchHistoryService
        {
            public Task AddSearchTermAsync(string term) => Task.CompletedTask;
            public Task<IEnumerable<string>> GetSuggestedTermsAsync(int maxCount) => Task.FromResult(Enumerable.Empty<string>());
        }

        private class MockWatchlistService : IWatchlistService
        {
            public event EventHandler? WatchlistUpdated;
            public Task AddAsync(string id) => Task.CompletedTask;
            public Task RemoveAsync(string id) => Task.CompletedTask;
            public Task<HashSet<string>> GetWatchlistIdsAsync() => Task.FromResult(new HashSet<string>());
            public bool IsOnWatchlist(string id) => false;
        }

        private MediaItemData CreateEpisode(string id, string name, string subtitle = "")
        {
            return new MediaItemData
            {
                Id = id,
                Name = name,
                Details = "",
                ImageUrl = "",
                IsLive = false,
                StreamUrl = $"http://test.com/{id}",
                Source = "Test",
                Type = MediaType.Video,
                Synopsis = "",
                Subtitle = subtitle
            };
        }

        private MediaItemData CreateSeason(string id, string name)
        {
            return new MediaItemData
            {
                Id = id,
                Name = name,
                Details = "",
                ImageUrl = "",
                IsLive = false,
                StreamUrl = "",
                Source = "Test",
                Type = MediaType.Brand,
                Synopsis = "",
                Subtitle = ""
            };
        }

        [Fact]
        public async Task PlayNextEpisode_WithinSeason_PlaysNextEpisode()
        {
            // Arrange
            var provider = new TestMediaProvider();
            var historyService = new TestHistoryService();
            var searchHistoryService = new TestSearchHistoryService();
            var dataService = new DataService(new[] { provider }, historyService, new MockWatchlistService(), new MediaItemCache());

            var season1Episodes = new List<MediaItemData>
            {
                CreateEpisode("show1|1|ep1", "Episode 1", "Series 1: Episode 1"),
                CreateEpisode("show1|1|ep2", "Episode 2", "Series 1: Episode 2"),
                CreateEpisode("show1|1|ep3", "Episode 3", "Series 1: Episode 3")
            };
            provider.SetChildren("show1|1", season1Episodes);

            var viewModel = new MainViewModel(dataService, searchHistoryService, new ScreensaverService());

            // Simulate opening a season and playing first episode
            var seasonData = CreateSeason("show1|1", "Season 1");
            var season = new MediaItem(seasonData);
            var programmeVm = new ProgrammeDetailViewModel(dataService, season);

            // Load children
            await Task.Delay(100); // Let LoadChildren complete

            // Simulate playing first episode from ProgrammeDetailViewModel
            viewModel.PlayItem(new MediaItem(season1Episodes[0]));

            // Set up the episode list context (this is what OpenProgramme.PlayRequested does)
            var currentEpisodeListField = typeof(MainViewModel).GetField("_currentEpisodeList",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentEpisodeListField?.SetValue(viewModel, season1Episodes.Select(d => new MediaItem(d)).ToList());

            var currentSeasonIdField = typeof(MainViewModel).GetField("_currentSeasonId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentSeasonIdField?.SetValue(viewModel, "show1|1");

            // Act - simulate stream ending on first episode
            var playNextMethod = typeof(MainViewModel).GetMethod("PlayNextEpisodeOrGoBack",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            playNextMethod?.Invoke(viewModel, null);

            // Allow async operations to complete
            await Task.Delay(100);

            // Assert - should now be playing second episode
            Assert.NotNull(viewModel.ActiveItem);
            Assert.Equal("show1|1|ep2", viewModel.ActiveItem.Id);
            Assert.Equal("Episode 2", viewModel.ActiveItem.Name);
        }

        [Fact]
        public async Task PlayNextEpisode_AtEndOfSeason_TransitionsToNextSeason()
        {
            // Arrange
            var provider = new TestMediaProvider();
            var historyService = new TestHistoryService();
            var searchHistoryService = new TestSearchHistoryService();
            var dataService = new DataService(new[] { provider }, historyService, new MockWatchlistService(), new MediaItemCache());

            var season1Episodes = new List<MediaItemData>
            {
                CreateEpisode("show1|1|ep1", "S1 Episode 1", "Series 1: Episode 1"),
                CreateEpisode("show1|1|ep2", "S1 Episode 2", "Series 1: Episode 2")
            };
            var season2Episodes = new List<MediaItemData>
            {
                CreateEpisode("show1|2|ep1", "S2 Episode 1", "Series 2: Episode 1"),
                CreateEpisode("show1|2|ep2", "S2 Episode 2", "Series 2: Episode 2")
            };

            provider.SetChildren("show1|1", season1Episodes);
            provider.SetChildren("show1|2", season2Episodes);

            var viewModel = new MainViewModel(dataService, searchHistoryService, new ScreensaverService());

            // Simulate playing last episode of season 1
            viewModel.PlayItem(new MediaItem(season1Episodes[1]));

            var currentEpisodeListField = typeof(MainViewModel).GetField("_currentEpisodeList",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentEpisodeListField?.SetValue(viewModel, season1Episodes.Select(d => new MediaItem(d)).ToList());

            var currentSeasonIdField = typeof(MainViewModel).GetField("_currentSeasonId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentSeasonIdField?.SetValue(viewModel, "show1|1");

            var currentShowIdField = typeof(MainViewModel).GetField("_currentShowId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentShowIdField?.SetValue(viewModel, "show1");

            // Act - simulate stream ending on last episode of season 1
            var playNextMethod = typeof(MainViewModel).GetMethod("PlayNextEpisodeOrGoBack",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            playNextMethod?.Invoke(viewModel, null);

            // Allow async operations to complete
            await Task.Delay(200);

            // Assert - should now be playing first episode of season 2
            Assert.NotNull(viewModel.ActiveItem);
            Assert.Equal("show1|2|ep1", viewModel.ActiveItem.Id);
            Assert.Equal("S2 Episode 1", viewModel.ActiveItem.Name);
        }

        [Fact]
        public async Task PlayNextEpisode_AtEndOfLastSeason_NavigatesBack()
        {
            // Arrange
            var provider = new TestMediaProvider();
            var historyService = new TestHistoryService();
            var searchHistoryService = new TestSearchHistoryService();
            var dataService = new DataService(new[] { provider }, historyService, new MockWatchlistService(), new MediaItemCache());

            var season3Episodes = new List<MediaItemData>
            {
                CreateEpisode("show1|3|ep1", "S3 Episode 1", "Series 3: Episode 1"),
                CreateEpisode("show1|3|ep2", "S3 Episode 2", "Series 3: Episode 2")
            };

            provider.SetChildren("show1|3", season3Episodes);
            // Season 4 does not exist - no children for "show1|4"

            var viewModel = new MainViewModel(dataService, searchHistoryService, new ScreensaverService());

            // Simulate playing last episode of season 3
            viewModel.PlayItem(new MediaItem(season3Episodes[1]));

            var currentEpisodeListField = typeof(MainViewModel).GetField("_currentEpisodeList",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentEpisodeListField?.SetValue(viewModel, season3Episodes.Select(d => new MediaItem(d)).ToList());

            var currentSeasonIdField = typeof(MainViewModel).GetField("_currentSeasonId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentSeasonIdField?.SetValue(viewModel, "show1|3");

            var currentShowIdField = typeof(MainViewModel).GetField("_currentShowId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentShowIdField?.SetValue(viewModel, "show1");

            // Push a test ViewModel to navigation stack so we can verify PopViewModel was called
            viewModel.PushViewModel(new ShowingVideoPlayerViewModel());
            var initialStackCount = viewModel.NavigationHistory.Count;

            // Act - simulate stream ending on last episode of last season
            var playNextMethod = typeof(MainViewModel).GetMethod("PlayNextEpisodeOrGoBack",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            playNextMethod?.Invoke(viewModel, null);

            // Allow async operations to complete
            await Task.Delay(200);

            // Assert - should have navigated back (popped from navigation stack)
            Assert.True(viewModel.NavigationHistory.Count < initialStackCount,
                "Navigation stack should have been popped");
        }

        [Fact]
        public async Task GetNextSeasonId_ValidSeasonId_ReturnsNextSeason()
        {
            // Arrange
            var provider = new TestMediaProvider();
            var historyService = new TestHistoryService();
            var searchHistoryService = new TestSearchHistoryService();
            var dataService = new DataService(new[] { provider }, historyService, new MockWatchlistService(), new MediaItemCache());
            var viewModel = new MainViewModel(dataService, searchHistoryService, new ScreensaverService());

            var getNextSeasonIdMethod = typeof(MainViewModel).GetMethod("GetNextSeasonId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act & Assert
            var result1 = getNextSeasonIdMethod?.Invoke(viewModel, new object[] { "show123|1" }) as string;
            Assert.Equal("show123|2", result1);

            var result2 = getNextSeasonIdMethod?.Invoke(viewModel, new object[] { "myshow|5" }) as string;
            Assert.Equal("myshow|6", result2);

            var result3 = getNextSeasonIdMethod?.Invoke(viewModel, new object[] { "show|10" }) as string;
            Assert.Equal("show|11", result3);
        }

        [Fact]
        public async Task GetNextSeasonId_InvalidSeasonId_ReturnsNull()
        {
            // Arrange
            var provider = new TestMediaProvider();
            var historyService = new TestHistoryService();
            var searchHistoryService = new TestSearchHistoryService();
            var dataService = new DataService(new[] { provider }, historyService, new MockWatchlistService(), new MediaItemCache());
            var viewModel = new MainViewModel(dataService, searchHistoryService, new ScreensaverService());

            var getNextSeasonIdMethod = typeof(MainViewModel).GetMethod("GetNextSeasonId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            // Act & Assert - no pipe separator
            var result1 = getNextSeasonIdMethod?.Invoke(viewModel, new object[] { "show123" }) as string;
            Assert.Null(result1);

            // Act & Assert - non-numeric season
            var result2 = getNextSeasonIdMethod?.Invoke(viewModel, new object[] { "show|abc" }) as string;
            Assert.Null(result2);

            // Act & Assert - null input
            var result3 = getNextSeasonIdMethod?.Invoke(viewModel, new object?[] { null }) as string;
            Assert.Null(result3);
        }

        [Fact]
        public async Task PlayNextEpisode_FlatEpisodeList_PlaysNextEpisodeWithinList()
        {
            // Arrange - single season show without season structure (no | in IDs)
            var provider = new TestMediaProvider();
            var historyService = new TestHistoryService();
            var searchHistoryService = new TestSearchHistoryService();
            var dataService = new DataService(new[] { provider }, historyService, new MockWatchlistService(), new MediaItemCache());

            var episodes = new List<MediaItemData>
            {
                CreateEpisode("show1-ep1", "Episode 1", "Episode 1"),
                CreateEpisode("show1-ep2", "Episode 2", "Episode 2"),
                CreateEpisode("show1-ep3", "Episode 3", "Episode 3")
            };
            provider.SetChildren("show1", episodes);

            var viewModel = new MainViewModel(dataService, searchHistoryService, new ScreensaverService());

            // Simulate playing first episode
            viewModel.PlayItem(new MediaItem(episodes[0]));

            var currentEpisodeListField = typeof(MainViewModel).GetField("_currentEpisodeList",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentEpisodeListField?.SetValue(viewModel, episodes.Select(d => new MediaItem(d)).ToList());

            // Set season ID without pipe (flat structure)
            var currentSeasonIdField = typeof(MainViewModel).GetField("_currentSeasonId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentSeasonIdField?.SetValue(viewModel, "show1");

            // Act - simulate stream ending on first episode
            var playNextMethod = typeof(MainViewModel).GetMethod("PlayNextEpisodeOrGoBack",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            playNextMethod?.Invoke(viewModel, null);

            // Allow async operations to complete
            await Task.Delay(100);

            // Assert - should play next episode in list
            Assert.NotNull(viewModel.ActiveItem);
            Assert.Equal("show1-ep2", viewModel.ActiveItem.Id);
            Assert.Equal("Episode 2", viewModel.ActiveItem.Name);
        }

        [Fact]
        public async Task PlayNextEpisode_FlatEpisodeListLastEpisode_NavigatesBack()
        {
            // Arrange - last episode in a flat list (no seasons)
            var provider = new TestMediaProvider();
            var historyService = new TestHistoryService();
            var searchHistoryService = new TestSearchHistoryService();
            var dataService = new DataService(new[] { provider }, historyService, new MockWatchlistService(), new MediaItemCache());

            var episodes = new List<MediaItemData>
            {
                CreateEpisode("show1-ep1", "Episode 1", "Episode 1"),
                CreateEpisode("show1-ep2", "Episode 2", "Episode 2")
            };
            provider.SetChildren("show1", episodes);

            var viewModel = new MainViewModel(dataService, searchHistoryService, new ScreensaverService());

            // Simulate playing last episode
            viewModel.PlayItem(new MediaItem(episodes[1]));

            var currentEpisodeListField = typeof(MainViewModel).GetField("_currentEpisodeList",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentEpisodeListField?.SetValue(viewModel, episodes.Select(d => new MediaItem(d)).ToList());

            var currentSeasonIdField = typeof(MainViewModel).GetField("_currentSeasonId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentSeasonIdField?.SetValue(viewModel, "show1");

            // Push a test ViewModel to navigation stack
            viewModel.PushViewModel(new ShowingVideoPlayerViewModel());
            var initialStackCount = viewModel.NavigationHistory.Count;

            // Act - simulate stream ending on last episode
            var playNextMethod = typeof(MainViewModel).GetMethod("PlayNextEpisodeOrGoBack",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            playNextMethod?.Invoke(viewModel, null);

            // Allow async operations to complete
            await Task.Delay(100);

            // Assert - should have navigated back (no season to transition to)
            Assert.True(viewModel.NavigationHistory.Count < initialStackCount,
                "Navigation stack should have been popped when reaching end of flat episode list");
        }

        [Fact]
        public async Task PlayNextEpisode_AudioContent_TransitionsToNextSeason()
        {
            // Arrange - test with audio content (audiobooks, music albums, etc.)
            var provider = new TestMediaProvider();
            var historyService = new TestHistoryService();
            var searchHistoryService = new TestSearchHistoryService();
            var dataService = new DataService(new[] { provider }, historyService, new MockWatchlistService(), new MediaItemCache());

            var season1Tracks = new List<MediaItemData>
            {
                new MediaItemData
                {
                    Id = "album1|1|track1",
                    Name = "Track 1",
                    Details = "",
                    ImageUrl = "",
                    IsLive = false,
                    StreamUrl = "http://test.com/track1",
                    Source = "Test",
                    Type = MediaType.Audio,  // Audio type instead of Video
                    Synopsis = "",
                    Subtitle = "Disc 1: Track 1"
                },
                new MediaItemData
                {
                    Id = "album1|1|track2",
                    Name = "Track 2",
                    Details = "",
                    ImageUrl = "",
                    IsLive = false,
                    StreamUrl = "http://test.com/track2",
                    Source = "Test",
                    Type = MediaType.Audio,
                    Synopsis = "",
                    Subtitle = "Disc 1: Track 2"
                }
            };

            var season2Tracks = new List<MediaItemData>
            {
                new MediaItemData
                {
                    Id = "album1|2|track1",
                    Name = "Track 1",
                    Details = "",
                    ImageUrl = "",
                    IsLive = false,
                    StreamUrl = "http://test.com/disc2track1",
                    Source = "Test",
                    Type = MediaType.Audio,
                    Synopsis = "",
                    Subtitle = "Disc 2: Track 1"
                }
            };

            provider.SetChildren("album1|1", season1Tracks);
            provider.SetChildren("album1|2", season2Tracks);

            var viewModel = new MainViewModel(dataService, searchHistoryService, new ScreensaverService());

            // Simulate playing last track of disc 1
            viewModel.PlayItem(new MediaItem(season1Tracks[1]));

            var currentEpisodeListField = typeof(MainViewModel).GetField("_currentEpisodeList",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentEpisodeListField?.SetValue(viewModel, season1Tracks.Select(d => new MediaItem(d)).ToList());

            var currentSeasonIdField = typeof(MainViewModel).GetField("_currentSeasonId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentSeasonIdField?.SetValue(viewModel, "album1|1");

            var currentShowIdField = typeof(MainViewModel).GetField("_currentShowId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentShowIdField?.SetValue(viewModel, "album1");

            // Act - simulate stream ending on last track of disc 1
            var playNextMethod = typeof(MainViewModel).GetMethod("PlayNextEpisodeOrGoBack",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            playNextMethod?.Invoke(viewModel, null);

            // Allow async operations to complete
            await Task.Delay(200);

            // Assert - should now be playing first track of disc 2
            Assert.NotNull(viewModel.ActiveItem);
            Assert.Equal("album1|2|track1", viewModel.ActiveItem.Id);
            Assert.Equal(MediaType.Audio, viewModel.ActiveItem.Type);
        }
    }
}
