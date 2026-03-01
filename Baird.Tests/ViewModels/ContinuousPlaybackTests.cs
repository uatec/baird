using System.Collections.Generic;
using System.Threading.Tasks;
using Baird.Models;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class ContinuousPlaybackTests
    {
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
            var app = AppTestObject.Create();

            var season1Episodes = new List<MediaItemData>
            {
                CreateEpisode("show1|1|ep1", "Episode 1", "Series 1: Episode 1"),
                CreateEpisode("show1|1|ep2", "Episode 2", "Series 1: Episode 2"),
                CreateEpisode("show1|1|ep3", "Episode 3", "Series 1: Episode 3")
            };
            app.SetupProviderData("show1|1", season1Episodes);

            var player = app.VideoPlayer;

            // Simulate user opening season and playing first episode
            player.PlayItem(season1Episodes[0]);
            player.SetCurrentEpisodeContext(season1Episodes, seasonId: "show1|1", showId: "show1");

            // Act - user finishes watching the first episode
            await player.SimulatePlaybackEndingAndPlayNext();

            // Assert - application automatically plays the second episode
            Assert.NotNull(app.CurrentActiveItem);
            Assert.Equal("show1|1|ep2", app.CurrentActiveItem.Id);
            Assert.Equal("Episode 2", app.CurrentActiveItem.Name);
        }

        [Fact]
        public async Task PlayNextEpisode_AtEndOfSeason_TransitionsToNextSeason()
        {
            // Arrange
            var app = AppTestObject.Create();

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

            app.SetupProviderData("show1|1", season1Episodes);
            app.SetupProviderData("show1|2", season2Episodes);

            var player = app.VideoPlayer;

            // Simulate user playing last episode of season 1
            player.PlayItem(season1Episodes[1]);
            player.SetCurrentEpisodeContext(season1Episodes, seasonId: "show1|1", showId: "show1");

            // Act - user finishes watching the last episode of season 1
            await player.SimulatePlaybackEndingAndPlayNext();

            // Assert - application automatically plays the first episode of season 2
            Assert.NotNull(app.CurrentActiveItem);
            Assert.Equal("show1|2|ep1", app.CurrentActiveItem.Id);
            Assert.Equal("S2 Episode 1", app.CurrentActiveItem.Name);
        }

        [Fact]
        public async Task PlayNextEpisode_AtEndOfLastSeason_NavigatesBack()
        {
            // Arrange
            var app = AppTestObject.Create();

            var season3Episodes = new List<MediaItemData>
            {
                CreateEpisode("show1|3|ep1", "S3 Episode 1", "Series 3: Episode 1"),
                CreateEpisode("show1|3|ep2", "S3 Episode 2", "Series 3: Episode 2")
            };

            app.SetupProviderData("show1|3", season3Episodes);
            // Season 4 does not exist

            var player = app.VideoPlayer;

            // Simulate user playing last episode of season 3
            player.PlayItem(season3Episodes[1]);
            player.SetCurrentEpisodeContext(season3Episodes, seasonId: "show1|3", showId: "show1");

            // Push a page so we can verify we go back
            app.PushView(new ShowingVideoPlayerViewModel());
            var initialStackCount = app.NavigationStackCount;

            // Act - user finishes watching the last episode overall
            await player.SimulatePlaybackEndingAndPlayNext();

            // Assert - application navigates back out of the player since there's nothing left
            Assert.True(app.NavigationStackCount < initialStackCount, "Navigation stack should have been popped");
        }

        [Fact]
        public async Task PlayNextEpisode_FlatEpisodeList_PlaysNextEpisodeWithinList()
        {
            // Arrange
            var app = AppTestObject.Create();

            var episodes = new List<MediaItemData>
            {
                CreateEpisode("show1-ep1", "Episode 1", "Episode 1"),
                CreateEpisode("show1-ep2", "Episode 2", "Episode 2"),
                CreateEpisode("show1-ep3", "Episode 3", "Episode 3")
            };
            app.SetupProviderData("show1", episodes);

            var player = app.VideoPlayer;

            // Simulate user playing first episode of a flat list
            player.PlayItem(episodes[0]);
            player.SetCurrentEpisodeContext(episodes, seasonId: "show1", showId: "show1");

            // Act - user finishes watching the first episode
            await player.SimulatePlaybackEndingAndPlayNext();

            // Assert - application automatically plays the second episode
            Assert.NotNull(app.CurrentActiveItem);
            Assert.Equal("show1-ep2", app.CurrentActiveItem.Id);
            Assert.Equal("Episode 2", app.CurrentActiveItem.Name);
        }

        [Fact]
        public async Task PlayNextEpisode_FlatEpisodeListLastEpisode_NavigatesBack()
        {
            // Arrange
            var app = AppTestObject.Create();

            var episodes = new List<MediaItemData>
            {
                CreateEpisode("show1-ep1", "Episode 1", "Episode 1"),
                CreateEpisode("show1-ep2", "Episode 2", "Episode 2")
            };
            app.SetupProviderData("show1", episodes);

            var player = app.VideoPlayer;

            // Simulate user playing last episode of a flat list
            player.PlayItem(episodes[1]);
            player.SetCurrentEpisodeContext(episodes, seasonId: "show1", showId: "show1");

            app.PushView(new ShowingVideoPlayerViewModel());
            var initialStackCount = app.NavigationStackCount;

            // Act - user finishes watching the last episode
            await player.SimulatePlaybackEndingAndPlayNext();

            // Assert - application navigates back
            Assert.True(app.NavigationStackCount < initialStackCount, "Navigation stack should have been popped when reaching end of flat episode list");
        }

        [Fact]
        public async Task PlayNextEpisode_AudioContent_TransitionsToNextSeason()
        {
            // Arrange
            var app = AppTestObject.Create();

            var season1Tracks = new List<MediaItemData>
            {
                new MediaItemData { Id = "album1|1|track1", Name = "Track 1", Type = MediaType.Audio, StreamUrl = "test", Details = "", ImageUrl = "", IsLive = false, Source = "Test", Synopsis = "", Subtitle = "" },
                new MediaItemData { Id = "album1|1|track2", Name = "Track 2", Type = MediaType.Audio, StreamUrl = "test", Details = "", ImageUrl = "", IsLive = false, Source = "Test", Synopsis = "", Subtitle = "" }
            };

            var season2Tracks = new List<MediaItemData>
            {
                new MediaItemData { Id = "album1|2|track1", Name = "Track 1", Type = MediaType.Audio, StreamUrl = "test", Details = "", ImageUrl = "", IsLive = false, Source = "Test", Synopsis = "", Subtitle = "" }
            };

            app.SetupProviderData("album1|1", season1Tracks);
            app.SetupProviderData("album1|2", season2Tracks);

            var player = app.VideoPlayer;

            // Simulate playing last track of disc 1
            player.PlayItem(season1Tracks[1]);
            player.SetCurrentEpisodeContext(season1Tracks, seasonId: "album1|1", showId: "album1");

            // Act - user finishes listening to the last track of disc 1
            await player.SimulatePlaybackEndingAndPlayNext();

            // Assert - application automatically plays first track of disc 2
            Assert.NotNull(app.CurrentActiveItem);
            Assert.Equal("album1|2|track1", app.CurrentActiveItem.Id);
            Assert.Equal(MediaType.Audio, app.CurrentActiveItem.Type);
        }
    }
}
