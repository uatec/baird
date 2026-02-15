using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    public class DataServiceTests
    {
        // Manual Mocks
        class MockMediaProvider : IMediaProvider
        {
            public string Name { get; set; } = "MockProvider";
            public List<MediaItem> MemoryItems { get; set; } = new List<MediaItem>();

            public Task<IEnumerable<MediaItem>> GetListingAsync() => Task.FromResult<IEnumerable<MediaItem>>(MemoryItems);

            public Task<IEnumerable<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken = default)
                => Task.FromResult<IEnumerable<MediaItem>>(MemoryItems.Where(x => x.Name.Contains(query)));

            public Task<IEnumerable<MediaItem>> GetChildrenAsync(string id) => Task.FromResult(Enumerable.Empty<MediaItem>());

            public Task<MediaItem?> GetItemAsync(string id)
            {
                return Task.FromResult(MemoryItems.FirstOrDefault(x => x.Id == id));
            }
        }

        class MockHistoryService : IHistoryService
        {
            public List<HistoryItem> History { get; set; } = new List<HistoryItem>();

            public Task UpsertAsync(MediaItem media, TimeSpan position, TimeSpan duration)
            {
                var existing = History.FirstOrDefault(x => x.Id == media.Id);
                if (existing == null)
                {
                    existing = new HistoryItem { Id = media.Id };
                    History.Add(existing);
                }
                existing.LastPosition = position;
                existing.Duration = duration;
                return Task.CompletedTask;
            }

            public Task<List<HistoryItem>> GetHistoryAsync() => Task.FromResult(History);

            public HistoryItem? GetProgress(string id) => History.FirstOrDefault(x => x.Id == id);
            public Task ClearHistoryAsync() => Task.CompletedTask;
        }

        class MockWatchlistService : IWatchlistService
        {
            public event EventHandler? WatchlistUpdated;
            public Task AddAsync(MediaItem item) => Task.CompletedTask;
            public Task RemoveAsync(string id) => Task.CompletedTask;
            public Task<List<MediaItem>> GetWatchlistAsync() => Task.FromResult(new List<MediaItem>());
            public bool IsOnWatchlist(string id) => false;
        }

        [Fact]
        public async Task GetHistoryItemsAsync_HydratesItemsCorrectly()
        {
            // Arrange
            var provider = new MockMediaProvider();
            var item1 = new MediaItem
            {
                Id = "item1",
                Name = "Movie 1",
                Source = "Mock",
                Type = MediaType.Video,
                Details = "Details 1",
                ImageUrl = "http://mock/1.jpg",
                IsLive = false,
                Synopsis = "Synopsis 1",
                Subtitle = "Subtitle 1"
            };
            var item2 = new MediaItem
            {
                Id = "item2",
                Name = "Movie 2",
                Source = "Mock",
                Type = MediaType.Video,
                Details = "Details 2",
                ImageUrl = "http://mock/2.jpg",
                IsLive = false,
                Synopsis = "Synopsis 2",
                Subtitle = "Subtitle 2"
            };
            provider.MemoryItems.Add(item1);
            provider.MemoryItems.Add(item2);

            var historyService = new MockHistoryService();
            historyService.History.Add(new HistoryItem { Id = "item1", LastPosition = TimeSpan.FromMinutes(10), Duration = TimeSpan.FromMinutes(100) });
            // item2 has no history
            // item3 has history but no media (deleted item)
            historyService.History.Add(new HistoryItem { Id = "item3", LastPosition = TimeSpan.FromMinutes(5) });

            var dataService = new DataService(new[] { provider }, historyService, new MockWatchlistService());

            // Act
            var results = (await dataService.GetHistoryItemsAsync()).ToList();

            // Assert
            Assert.Single(results); // Only item1 should be returned (item2 not in history, item3 not in provider)

            var resultItem = results[0];
            Assert.Equal("item1", resultItem.Id);
            Assert.Equal("Movie 1", resultItem.Name);
            Assert.NotNull(resultItem.History);
            Assert.Equal(TimeSpan.FromMinutes(10), resultItem.History.LastPosition);
        }

        [Fact]
        public async Task UpsertHistoryAsync_UpdatesHistoryServiceAndLocalItem()
        {
            // Arrange
            var provider = new MockMediaProvider();
            var historyService = new MockHistoryService();
            var dataService = new DataService(new[] { provider }, historyService, new MockWatchlistService());

            var item = new MediaItem
            {
                Id = "test1",
                Name = "Test 1",
                Source = "Mock",
                Type = MediaType.Video,
                Details = "Details Test",
                ImageUrl = "http://mock/test.jpg",
                IsLive = false,
                Synopsis = "Synopsis Test",
                Subtitle = "Subtitle Test"
            };

            // Act
            await dataService.UpsertHistoryAsync(item, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

            // Assert
            Assert.Single(historyService.History);
            Assert.Equal("test1", historyService.History[0].Id);
            Assert.Equal(TimeSpan.FromMinutes(5), historyService.History[0].LastPosition);

            // Check local item updated
            Assert.NotNull(item.History);
            Assert.Equal(TimeSpan.FromMinutes(5), item.History.LastPosition);
        }

        [Fact]
        public async Task UpsertHistoryAsync_RaisesHistoryUpdatedEvent()
        {
            // Arrange
            var provider = new MockMediaProvider();
            var historyService = new MockHistoryService();
            var dataService = new DataService(new[] { provider }, historyService, new MockWatchlistService());

            var item = new MediaItem
            {
                Id = "test1",
                Name = "Test 1",
                Source = "Mock",
                Type = MediaType.Video,
                Details = "Details Test",
                ImageUrl = "http://mock/test.jpg",
                IsLive = false,
                Synopsis = "Synopsis Test",
                Subtitle = "Subtitle Test"
            };

            var eventRaised = false;
            dataService.HistoryUpdated += (sender, args) => eventRaised = true;

            // Act
            await dataService.UpsertHistoryAsync(item, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

            // Assert
            Assert.True(eventRaised, "HistoryUpdated event should be raised when history is updated");
        }
    }
}
