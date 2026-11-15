using System;
using System.Threading.Tasks;
using Baird.Models;
using Baird.Services;
using Baird.ViewModels;
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

        private static MediaItemViewModel Media(string id, bool live) =>
            new(new MediaItemData { Id = id, Name = id, Details = "", ImageUrl = "", IsLive = live, Source = "Test", Type = live ? MediaType.Channel : MediaType.Video, Synopsis = "", Subtitle = "", StreamUrl = "http://test" });

        [Fact]
        public async Task A_live_channel_watched_for_a_minute_is_never_finished()
        {
            // A channel has no end: it is saved with a duration of 0, which used to look like "past the end" (finished) and
            // kept every watched channel out of Continue watching.
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hist-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var service = new JsonHistoryService(path);
                await service.UpsertAsync(Media("itv1", live: true), TimeSpan.FromMinutes(1), TimeSpan.Zero);
                var h = service.GetProgress("itv1")!;
                Assert.False(h.IsFinished);
                Assert.Equal(TimeSpan.FromMinutes(1), h.LastPosition);
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
        }

        [Fact]
        public async Task A_video_with_no_known_length_that_played_to_the_end_is_still_finished()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hist-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var service = new JsonHistoryService(path);
                await service.UpsertAsync(Media("clip", live: false), TimeSpan.FromMinutes(3), TimeSpan.Zero);
                Assert.True(service.GetProgress("clip")!.IsFinished);
            }
            finally { try { System.IO.File.Delete(path); } catch { } }
        }
    }
}
