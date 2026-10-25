using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baird.Models;
using Baird.Services;
using Baird.Tests.Mocks;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class EpgViewModelTests
    {
        private (EpgViewModel vm, MockEpgService mockEpg, MockMediaDataCache mockData) CreateTestContext(IEnumerable<MediaItemData>? streamingItems = null)
        {
            var mockEpg = new MockEpgService();
            var mockCache = new MockMediaDataCache();
            var mockWatchlist = new MockWatchlistService();
            var mockHistory = new JsonHistoryService();
            var mockProvider = new Baird.Tests.Mocks.MockMediaProvider("Streaming", streamingItems ?? Enumerable.Empty<MediaItemData>());

            var dataService = new DataService(
                new List<IMediaProvider> { mockProvider },
                mockHistory,
                mockWatchlist,
                new MediaItemCache(),
                mockCache);

            var channels = new List<MediaItemViewModel>
            {
                new MediaItemViewModel(new MediaItemData
                {
                    Id = "chan-1",
                    Name = "BBC One HD",
                    ChannelNumber = "1",
                    IsLive = true,
                    Source = "Live TV",
                    Type = MediaType.Channel,
                    Details = "",
                    Synopsis = "",
                    Subtitle = "",
                    ImageUrl = ""
                }),
                new MediaItemViewModel(new MediaItemData
                {
                    Id = "chan-2",
                    Name = "BBC Two HD",
                    ChannelNumber = "2",
                    IsLive = true,
                    Source = "Live TV",
                    Type = MediaType.Channel,
                    Details = "",
                    Synopsis = "",
                    Subtitle = "",
                    ImageUrl = ""
                })
            };

            var vm = new EpgViewModel(dataService, mockEpg, () => channels);
            return (vm, mockEpg, mockCache);
        }

        [Fact]
        public async Task Typed_digits_list_only_the_channels_whose_number_starts_with_them()
        {
            var (vm, _, _) = CreateTestContext();
            await vm.RefreshAsync();
            Assert.Equal(2, vm.Channels.Count);

            vm.AppendChannelDigit("2");

            Assert.Equal("2", Assert.Single(vm.Channels).Channel.ChannelNumber);
            Assert.True(vm.HasChannelNumberFilter);

            Assert.True(vm.BackspaceChannelDigit());
            Assert.Equal(2, vm.Channels.Count);
            Assert.False(vm.BackspaceChannelDigit());
        }

        [Fact]
        public async Task RefreshAsync_PopulatesChannelsAndSchedules()
        {
            var (vm, mockEpg, _) = CreateTestContext();
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            mockEpg.MockSchedules["chan-1"] = new List<EpgProgram>
            {
                new EpgProgram
                {
                    Id = "prog-1",
                    ChannelId = "chan-1",
                    Title = "BBC News",
                    StartUnix = nowUnix - 600,
                    StopUnix = nowUnix + 1200
                },
                new EpgProgram
                {
                    Id = "prog-2",
                    ChannelId = "chan-1",
                    Title = "Weather",
                    StartUnix = nowUnix + 1200,
                    StopUnix = nowUnix + 1500
                }
            };

            await vm.RefreshAsync();

            Assert.True(vm.Channels.Count >= 2);
            Assert.NotNull(vm.SelectedChannel);
            Assert.Equal("chan-1", vm.SelectedChannel.Channel.Id);
            Assert.NotNull(vm.SelectedChannel.Now);
            Assert.Equal("BBC News", vm.SelectedChannel.Now.Title);
            Assert.NotNull(vm.SelectedChannel.Next);
            Assert.Equal("Weather", vm.SelectedChannel.Next.Title);
            Assert.NotNull(vm.SelectedProgram);
            Assert.Equal("BBC News", vm.SelectedProgram.Title);
        }

        [Fact]
        public void PlayChannelCommand_FiresPlayRequested()
        {
            var (vm, _, _) = CreateTestContext();
            MediaItemViewModel? played = null;
            vm.PlayRequested += (s, item) => played = item;

            var channel = new MediaItemViewModel(new MediaItemData
            {
                Id = "chan-99",
                Name = "ITV1",
                IsLive = true,
                Source = "Live TV",
                Type = MediaType.Channel,
                Details = "",
                Synopsis = "",
                Subtitle = "",
                ImageUrl = ""
            });

            vm.PlayChannelCommand.Execute(channel).Subscribe();

            Assert.NotNull(played);
            Assert.Equal("chan-99", played.Id);
        }

        [Fact]
        public void StreamingShow_LoadsEpisodesWithParity()
        {
            var show = new MediaItemViewModel(new MediaItemData
            {
                Id = "show-1",
                Name = "Severance",
                IsLive = false,
                Source = "Jellyfin",
                Type = MediaType.Brand,
                Details = "Sci-Fi",
                Synopsis = "Mark leads a team whose memories are divided.",
                Subtitle = "Season 1",
                ImageUrl = ""
            });

            var scheduleVm = new ChannelScheduleViewModel(show);
            var ep1 = new MediaItemViewModel(new MediaItemData
            {
                Id = "ep-1",
                Name = "Good News About Hell",
                IsLive = false,
                Source = "Jellyfin",
                Type = MediaType.Video,
                Details = "S1:E1",
                Synopsis = "Mark Scout leads a team at Lumon.",
                Subtitle = "S1:E1",
                ImageUrl = "",
                Duration = TimeSpan.FromMinutes(55)
            });

            var ep2 = new MediaItemViewModel(new MediaItemData
            {
                Id = "ep-2",
                Name = "Half Loop",
                IsLive = false,
                Source = "Jellyfin",
                Type = MediaType.Video,
                Details = "S1:E2",
                Synopsis = "The macrodata team trains Helly.",
                Subtitle = "S1:E2",
                ImageUrl = "",
                Duration = TimeSpan.FromMinutes(53)
            });

            scheduleVm.LoadStreamingEpisodes(new[] { ep1, ep2 });

            Assert.False(scheduleVm.IsLive);
            Assert.Equal("Jellyfin", scheduleVm.Source);
            Assert.Equal(2, scheduleVm.Schedule.Count);
            Assert.NotNull(scheduleVm.Now);
            Assert.Equal("Good News About Hell", scheduleVm.Now.Title);
            Assert.NotNull(scheduleVm.Next);
            Assert.Equal("Half Loop", scheduleVm.Next.Title);
            Assert.True(scheduleVm.Now.IsStreamingEpisode);
            Assert.Equal(ep1, scheduleVm.Now.MediaItem);
        }

        [Fact]
        public async Task Filter_SwitchesBetweenLiveAndStreaming()
        {
            var streamingItem = new MediaItemData
            {
                Id = "stream-1",
                Name = "Slow Horses",
                IsLive = false,
                Source = "Streaming",
                Type = MediaType.Brand,
                Details = "",
                Synopsis = "Spy series",
                Subtitle = "",
                ImageUrl = ""
            };

            var (vm, _, _) = CreateTestContext(new[] { streamingItem });

            await vm.RefreshAsync();

            // All
            Assert.Contains(vm.Channels, c => c.IsLive);
            Assert.Contains(vm.Channels, c => !c.IsLive);

            // Live TV only
            vm.SelectedFilter = "Live TV";
            Assert.All(vm.Channels, c => Assert.True(c.IsLive));

            // Streaming only
            vm.SelectedFilter = "Streaming";
            Assert.All(vm.Channels, c => Assert.False(c.IsLive));
        }

        [Fact]
        public void ChannelScheduleViewModel_ComputesNowAndNextCorrectly()
        {
            var channel = new MediaItemViewModel(new MediaItemData
            {
                Id = "chan-1",
                Name = "Channel 4",
                IsLive = true,
                Source = "Live TV",
                Type = MediaType.Channel,
                Details = "",
                Synopsis = "",
                Subtitle = "",
                ImageUrl = ""
            });

            var scheduleVm = new ChannelScheduleViewModel(channel);
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var progs = new List<EpgProgram>
            {
                new EpgProgram
                {
                    Id = "p1",
                    Title = "Past Show",
                    StartUnix = nowUnix - 3600,
                    StopUnix = nowUnix - 1800
                },
                new EpgProgram
                {
                    Id = "p2",
                    Title = "Current Show",
                    StartUnix = nowUnix - 1800,
                    StopUnix = nowUnix + 1800
                },
                new EpgProgram
                {
                    Id = "p3",
                    Title = "Future Show",
                    StartUnix = nowUnix + 1800,
                    StopUnix = nowUnix + 3600
                }
            };

            scheduleVm.UpdateSchedule(progs);

            Assert.NotNull(scheduleVm.Now);
            Assert.Equal("Current Show", scheduleVm.Now.Title);
            Assert.NotNull(scheduleVm.Next);
            Assert.Equal("Future Show", scheduleVm.Next.Title);
            Assert.Equal("Current Show", channel.CurrentProgrammeName);
        }

        [Fact]
        public void EpgProgram_ComputesProgressAndFormatting()
        {
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var prog = new EpgProgram
            {
                Id = "p-test",
                Title = "Grand Designs",
                StartUnix = nowUnix - 900, // 15 mins ago
                StopUnix = nowUnix + 900,  // 15 mins left (30m total)
                Description = "Kevin McCloud follows a couple building an eco-home."
            };

            Assert.True(prog.IsCurrent);
            Assert.InRange(prog.Progress, 0.45, 0.55);
            Assert.Equal(TimeSpan.FromMinutes(30), prog.Duration);
            Assert.Equal("30m", prog.FormattedDuration);
            Assert.Equal("Grand Designs", prog.DisplayTitle);
            Assert.Equal("Kevin McCloud follows a couple building an eco-home.", prog.DisplayDetails);
        }

        private class MockWatchlistService : IWatchlistService
        {
            public event EventHandler? WatchlistUpdated;
            public Task AddAsync(string id) => Task.CompletedTask;
            public Task RemoveAsync(string id) => Task.CompletedTask;
            public Task<HashSet<string>> GetWatchlistIdsAsync() => Task.FromResult(new HashSet<string>());
            public bool IsOnWatchlist(string id) => false;
        }
    }
}
