using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baird.Services;
using Xunit;

namespace Baird.Tests.Services
{
    /// <summary>Shapes copied from yt-dlp's flat output on 2026-10-26 (trimmed). yt-dlp is faked: nothing here touches the network.</summary>
    public class YouTubeServiceTests
    {
        private const string Channel = "UCHnyfMqiRRG1u-2MsSQLbXA";
        private const string ChannelLine = @"{""_type"":""url"",""id"":""" + Channel + @""",""channel_id"":""" + Channel + @""",""title"":""Veritasium"",""channel_follower_count"":21400000,""thumbnails"":[{""url"":""//yt3.ggpht.com/small""},{""url"":""//yt3.ggpht.com/big""}]}";
        private const string VideoLine = @"{""id"":""9M_QK4stCJU"",""title"":""Why People Are So Confident"",""duration"":1442,""uploader"":""Veritasium"",""webpage_url"":""https://www.youtube.com/watch?v=9M_QK4stCJU""}";

        private static string Playlist(string id, string title) => $@"{{""_type"":""url"",""id"":""{id}"",""title"":""{title}"",""url"":""https://www.youtube.com/playlist?list={id}""}}";
        private static string Video(string id) => $@"{{""id"":""{id}"",""title"":""Video {id}"",""duration"":60}}";

        private sealed class FakeYtDlp
        {
            public readonly List<string[]> Calls = new();
            private readonly Func<string[], string[]> _reply;
            public FakeYtDlp(Func<string[], string[]> reply) { _reply = reply; }
            public YtDlpRunner Runner => (args, _) => { var a = args.ToArray(); lock (Calls) Calls.Add(a); return Task.FromResult<IReadOnlyList<string>>(_reply(a)); };
        }

        [Fact]
        public async Task Search_ListsChannelsBeforeVideos_AsSeries()
        {
            var fake = new FakeYtDlp(a => a.Last().StartsWith("ytsearch") ? new[] { VideoLine } : new[] { ChannelLine });
            var results = (await new YouTubeService(fake.Runner).SearchAsync("veritasium")).ToList();

            Assert.Equal(2, results.Count);
            Assert.Equal(YouTubeService.ChannelPrefix + Channel, results[0].Id);
            Assert.Equal(MediaType.Brand, results[0].Type);
            Assert.Equal("21.4M subscribers", results[0].Details);
            Assert.Equal("https://yt3.ggpht.com/big", results[0].ImageUrl);
            Assert.Equal("", results[0].StreamUrl);
            Assert.Equal("9M_QK4stCJU", results[1].Id);
            Assert.Equal(MediaType.Video, results[1].Type);
        }

        [Fact]
        public void ParseChannel_IgnoresNonChannels()
        {
            Assert.Null(YouTubeService.ParseChannel(@"{""id"":""PLabc"",""title"":""A playlist""}"));
            Assert.Null(YouTubeService.ParseChannel("not json"));
        }

        [Fact]
        public void ParseVideo_SkipsLiveAndUpcoming_AndToleratesMissingUploader()
        {
            Assert.Null(YouTubeService.ParseVideo(@"{""id"":""a"",""title"":""t"",""live_status"":""is_upcoming""}"));
            var v = YouTubeService.ParseVideo(@"{""id"":""abc"",""title"":""t""}")!;
            Assert.Equal("https://www.youtube.com/watch?v=abc", v.StreamUrl);
            Assert.Equal("https://i.ytimg.com/vi/abc/hqdefault.jpg", v.ImageUrl);
        }

        [Fact]
        public async Task Channel_WithPlaylists_OpensToAllVideosThenPlaylists_SkippingDefaults()
        {
            var fake = new FakeYtDlp(_ => new[] { Playlist("PLcomputing", "Computing"), Playlist("PLfav", "Favorites"), Playlist("PLbio", "Biology") });
            var folders = (await new YouTubeService(fake.Runner).GetChildrenAsync(YouTubeService.ChannelPrefix + Channel)).ToList();

            Assert.Equal(new[] { "All videos", "Computing", "Biology" }, folders.Select(f => f.Name));
            Assert.All(folders, f => Assert.Equal(MediaType.Brand, f.Type));
            Assert.Equal($"{YouTubeService.ChannelPrefix}{Channel}|videos", folders[0].Id);
            Assert.Equal($"{YouTubeService.ChannelPrefix}{Channel}|list/PLcomputing", folders[1].Id);
        }

        [Fact]
        public async Task Channel_WithoutPlaylists_OpensStraightToItsVideos()
        {
            var fake = new FakeYtDlp(a => a.Last().EndsWith("/playlists") ? Array.Empty<string>() : new[] { Video("v1"), Video("v2") });
            var items = (await new YouTubeService(fake.Runner).GetChildrenAsync(YouTubeService.ChannelPrefix + Channel)).ToList();

            Assert.Equal(new[] { "v1", "v2" }, items.Select(i => i.Id));
            Assert.All(items, i => Assert.Equal(MediaType.Video, i.Type));
            Assert.Contains(fake.Calls, c => c.Last() == $"https://www.youtube.com/channel/{Channel}/videos" && c.Contains("1:50"));
        }

        [Fact]
        public async Task Folders_PageFiftyAtATime()
        {
            var fake = new FakeYtDlp(_ => new[] { Video("v") });
            var svc = new YouTubeService(fake.Runner);
            await svc.GetChildrenAsync($"{YouTubeService.ChannelPrefix}{Channel}|videos");
            await svc.GetChildrenAsync($"{YouTubeService.ChannelPrefix}{Channel}|videos@3");
            await svc.GetChildrenAsync($"{YouTubeService.ChannelPrefix}{Channel}|list/PLbio@2");
            await svc.GetChildrenAsync($"{YouTubeService.ChannelPrefix}{Channel}@2");

            Assert.Contains(fake.Calls, c => c.Contains("1:50") && c.Last().EndsWith("/videos"));
            Assert.Contains(fake.Calls, c => c.Contains("101:150"));
            Assert.Contains(fake.Calls, c => c.Contains("51:100") && c.Last() == "https://www.youtube.com/playlist?list=PLbio");
            // A bare channel's second page is its videos, not its playlists again.
            Assert.Contains(fake.Calls, c => c.Contains("51:100") && c.Last() == $"https://www.youtube.com/channel/{Channel}/videos");
            Assert.DoesNotContain(fake.Calls, c => c.Last().EndsWith("/playlists"));
        }

        [Fact]
        public void NextPageId_OnlyWhenThePageWasFull()
        {
            var id = $"{YouTubeService.ChannelPrefix}{Channel}|videos";
            Assert.Equal(id + "@2", YouTubeService.NextPageId(id, YouTubeService.PageSize));
            Assert.Equal(id + "@3", YouTubeService.NextPageId(id + "@2", YouTubeService.PageSize));
            Assert.Equal(id + "@2", YouTubeService.NextPageId(id, YouTubeService.PageSize - 3)); // live streams dropped from the page
            Assert.Null(YouTubeService.NextPageId(id, YouTubeService.PageSize / 2));
        }

        [Fact]
        public async Task Children_IgnoresOtherIds_AndUnsafeOnes()
        {
            var fake = new FakeYtDlp(_ => new[] { Video("v") });
            var svc = new YouTubeService(fake.Runner);
            Assert.Empty(await svc.GetChildrenAsync("itvx:abc/123"));
            Assert.Empty(await svc.GetChildrenAsync("dQw4w9WgXcQ"));
            Assert.Empty(await svc.GetChildrenAsync(YouTubeService.ChannelPrefix + "UC x/../?evil=1|videos"));
            Assert.Empty(fake.Calls);
        }

        [Fact]
        public async Task YtDlpFailure_GivesNothing()
        {
            var svc = new YouTubeService((_, _) => throw new InvalidOperationException("no yt-dlp"));
            Assert.Empty(await svc.SearchAsync("x"));
            Assert.Empty(await svc.GetChildrenAsync(YouTubeService.ChannelPrefix + Channel));
        }
    }
}
