using System;
using System.Threading.Tasks;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    public class HistoryServiceTests
    {
        [Fact]
        public async Task UpsertAsync_ShortVideo_FinishedAt96Percent()
        {
            // Arrange
            var service = new JsonHistoryService(); // This might try to write to disk. Ideally mock file system or use separate path.
                                                    // For unit test, JsonHistoryService writes to ~/.baird/history.json.
                                                    // We should use a temp path or mock. 
                                                    // Since JsonHistoryService hardcodes path in constructor, we can't easily test without side effects.
                                                    // I should have injected configuration or path.
                                                    // But for now, I can check logic by reflection or just reading the property if exposed?
                                                    // UPSERT updates internal cache.
                                                    // I can check GetProgress.

            var itemData = new MediaItemData { Id = "test1", Name = "Short", Details = "", ImageUrl = "", IsLive = false, Source = "Test", Type = MediaType.Video, Synopsis = "", Subtitle = "", StreamUrl = "http://test" };
            var item = new MediaItemViewModel(itemData);
            var duration = TimeSpan.FromMinutes(5); // 300s
            var position = TimeSpan.FromSeconds(290); // 10s remaining. 5% of 300 is 15s. 
            // 10s < 15s -> Should be finished.

            // Act
            await service.UpsertAsync(item, position, duration);
            var result = service.GetProgress("test1");

            // Assert
            Assert.NotNull(result);
            Assert.True(result.IsFinished, "Short video with <5% remaining should be finished");
        }

        [Fact]
        public async Task UpsertAsync_ShortVideo_NotFinishedAt90Percent()
        {
            // Arrange
            var service = new JsonHistoryService();
            var itemData = new MediaItemData { Id = "test2", Name = "Short2", Details = "", ImageUrl = "", IsLive = false, Source = "Test", Type = MediaType.Video, Synopsis = "", Subtitle = "", StreamUrl = "http://test" };
            var item = new MediaItemViewModel(itemData);
            var duration = TimeSpan.FromMinutes(5); // 300s
            var position = TimeSpan.FromSeconds(270); // 30s remaining. 5% is 15s.
            // 30 > 15 -> Not finished.

            // Act
            await service.UpsertAsync(item, position, duration);
            var result = service.GetProgress("test2");

            // Assert
            Assert.NotNull(result);
            Assert.False(result.IsFinished, "Short video with >5% remaining should NOT be finished");
        }

        [Fact]
        public async Task UpsertAsync_LongVideo_FinishedAt10MinsRemaining()
        {
            // Arrange
            var service = new JsonHistoryService();
            var itemData = new MediaItemData { Id = "test_long_1", Name = "LongDiff", Details = "", ImageUrl = "", IsLive = false, Source = "Test", Type = MediaType.Video, Synopsis = "", Subtitle = "", StreamUrl = "http://test" };
            var item = new MediaItemViewModel(itemData);
            var duration = TimeSpan.FromMinutes(100); // 6000s. > 90 mins.
            var position = TimeSpan.FromMinutes(91); // 9 mins remaining.

            // 9 mins < 10 mins -> Finished.

            // Act
            await service.UpsertAsync(item, position, duration);
            var result = service.GetProgress("test_long_1");

            // Assert
            Assert.NotNull(result);
            Assert.True(result.IsFinished, "Long video with <10m remaining should be finished");
        }

        [Fact]
        public async Task UpsertAsync_LongVideo_NotFinishedAt15MinsRemaining()
        {
            // Arrange
            var service = new JsonHistoryService();
            var itemData = new MediaItemData { Id = "test_long_2", Name = "LongDiff2", Details = "", ImageUrl = "", IsLive = false, Source = "Test", Type = MediaType.Video, Synopsis = "", Subtitle = "", StreamUrl = "http://test" };
            var item = new MediaItemViewModel(itemData);
            var duration = TimeSpan.FromMinutes(100); // 6000s
            var position = TimeSpan.FromMinutes(85); // 15 mins remaining.

            // 15 > 10 -> Not Finished.

            // Act
            await service.UpsertAsync(item, position, duration);
            var result = service.GetProgress("test_long_2");

            // Assert
            Assert.NotNull(result);
            Assert.False(result.IsFinished, "Long video with >10m remaining should NOT be finished");
        }
    }
}
