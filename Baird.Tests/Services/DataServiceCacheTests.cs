using Baird.Models;
using Baird.Services;

namespace Baird.Tests.Services;

public class DataServiceCacheTests
{
    private class MockMediaProvider : IMediaProvider
    {
        public string Name { get; set; } = "Mock";
        public int CallCount { get; private set; } = 0;
        public MediaItem? ItemToReturn { get; set; }

        public Task<IEnumerable<MediaItem>> GetListingAsync() => Task.FromResult(Enumerable.Empty<MediaItem>());
        public Task<IEnumerable<MediaItem>> SearchAsync(string query, CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<MediaItem>());
        public Task<IEnumerable<MediaItem>> GetChildrenAsync(string id) => Task.FromResult(Enumerable.Empty<MediaItem>());

        public Task<MediaItem?> GetItemAsync(string id)
        {
            CallCount++;
            if (ItemToReturn != null && ItemToReturn.Id == id)
            {
                return Task.FromResult<MediaItem?>(ItemToReturn);
            }
            return Task.FromResult<MediaItem?>(null);
        }
    }

    private class MockHistoryService : IHistoryService
    {
        public Task UpsertAsync(MediaItem media, TimeSpan position, TimeSpan duration) => Task.CompletedTask;
        public Task<List<HistoryItem>> GetHistoryAsync() => Task.FromResult(new List<HistoryItem>());
        public HistoryItem? GetProgress(string id) => null;
    }

    [Fact]
    public async Task GetItemAsync_CachesItem_SubsequentCallsDoNotHitProvider()
    {
        // Arrange
        var provider = new MockMediaProvider();
        var item = new MediaItem
        {
            Id = "test1",
            Name = "Cached Item",
            Source = "Mock",
            Type = MediaType.Video,
            Details = "D",
            ImageUrl = "U",
            IsLive = false,
            Synopsis = "S",
            Subtitle = "Sub"
        };
        provider.ItemToReturn = item;

        var dataService = new DataService(new[] { provider }, new MockHistoryService());

        // Act
        // First call - should hit provider
        MediaItem? result1 = await dataService.GetItemAsync("test1");

        // Second call - should hit cache
        MediaItem? result2 = await dataService.GetItemAsync("test1");

        // Assert
        Assert.NotNull(result1);
        Assert.Same(item, result1); // Should return exact instance
        Assert.Same(result1, result2); // Second call should return same instance

        Assert.Equal(1, provider.CallCount); // Provider should only be called once
    }

    [Fact]
    public async Task GetItemAsync_ReturnsCorrectItem()
    {
        // Arrange
        var provider = new MockMediaProvider();
        var item = new MediaItem
        {
            Id = "test1",
            Name = "Cached Item",
            Source = "Mock",
            Type = MediaType.Video,
            Details = "D",
            ImageUrl = "U",
            IsLive = false,
            Synopsis = "S",
            Subtitle = "Sub"
        };
        provider.ItemToReturn = item;
        var dataService = new DataService(new[] { provider }, new MockHistoryService());

        // Act
        MediaItem? result = await dataService.GetItemAsync("test1");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test1", result.Id);
    }
}
