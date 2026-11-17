using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;
using Baird.Services;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.Services
{
    /// <summary>
    /// Ids no provider can supply (a Jellyfin item deleted months ago, a streaming title the service no longer lists) must not be
    /// asked about on every refresh of Home, and a provider being down must not be mistaken for the item being gone.
    /// </summary>
    public class DataServiceLookupTests
    {
        private sealed class CountingProvider : IMediaProvider
        {
            public string Name => "Counting";
            public readonly Dictionary<string, int> Asked = new();
            public Func<string, MediaItemData?> Answer = _ => null;
            public bool Down;
            public int Total => Asked.Values.Sum();
            public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult<IEnumerable<MediaItemData>>(Array.Empty<MediaItemData>());
            public Task<IEnumerable<MediaItemData>> SearchAsync(string query, CancellationToken cancellationToken = default) => Task.FromResult<IEnumerable<MediaItemData>>(Array.Empty<MediaItemData>());
            public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id) => Task.FromResult<IEnumerable<MediaItemData>>(Array.Empty<MediaItemData>());
            public Task<MediaItemData?> GetItemAsync(string id)
            {
                lock (Asked) Asked[id] = Asked.GetValueOrDefault(id) + 1;
                if (Down) throw new InvalidOperationException("503");
                return Task.FromResult(Answer(id));
            }
        }

        private sealed class History : IHistoryService
        {
            public List<HistoryItem> Items = new();
            public Task UpsertAsync(MediaItemViewModel media, TimeSpan position, TimeSpan duration) => Task.CompletedTask;
            public Task<List<HistoryItem>> GetHistoryAsync() => Task.FromResult(Items);
            public HistoryItem? GetProgress(string id) => Items.FirstOrDefault(i => i.Id == id);
            public Task ClearHistoryAsync() => Task.CompletedTask;
        }

        private sealed class NoWatchlist : IWatchlistService
        {
#pragma warning disable CS0067
            public event EventHandler? WatchlistUpdated;
#pragma warning restore CS0067
            public Task AddAsync(string id) => Task.CompletedTask;
            public Task RemoveAsync(string id) => Task.CompletedTask;
            public Task<HashSet<string>> GetWatchlistIdsAsync() => Task.FromResult(new HashSet<string>());
            public bool IsOnWatchlist(string id) => false;
        }

        private static MediaItemData Data(string id) => new() { Id = id, Name = id, Source = "Counting", Type = MediaType.Video, Details = "", ImageUrl = "", IsLive = false, Synopsis = "", Subtitle = "" };

        private static (DataService Svc, CountingProvider P, History H, Action<TimeSpan> Advance) Make()
        {
            var p = new CountingProvider(); var h = new History(); var t = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
            var svc = new DataService(new IMediaProvider[] { p }, h, new NoWatchlist(), new MediaItemCache(), new Baird.Tests.Mocks.MockMediaDataCache(), () => t);
            return (svc, p, h, d => t += d);
        }

        [Fact]
        public async Task An_id_every_provider_says_it_hasnt_got_is_asked_once_not_every_time()
        {
            var (svc, p, _, _) = Make();
            for (var i = 0; i < 5; i++) Assert.Null(await svc.GetItemAsync("gone"));
            Assert.Equal(1, p.Asked["gone"]);
        }

        [Fact]
        public async Task An_unknown_id_is_asked_again_after_twelve_hours()
        {
            var (svc, p, _, advance) = Make();
            await svc.GetItemAsync("gone");
            advance(DataService.UnknownFor - TimeSpan.FromMinutes(1)); await svc.GetItemAsync("gone");
            Assert.Equal(1, p.Asked["gone"]);
            advance(TimeSpan.FromMinutes(2)); await svc.GetItemAsync("gone");
            Assert.Equal(2, p.Asked["gone"]);
        }

        [Fact]
        public async Task A_provider_being_down_is_not_taken_for_the_item_being_gone()
        {
            var (svc, p, _, advance) = Make();
            p.Down = true;
            Assert.Null(await svc.GetItemAsync("maybe"));
            Assert.Null(await svc.GetItemAsync("maybe"));                    // not hammered while it is down
            Assert.Equal(1, p.Asked["maybe"]);

            p.Down = false; p.Answer = Data;                                  // it comes back
            advance(DataService.UnavailableFor + TimeSpan.FromSeconds(1));    // minutes later, not twelve hours
            Assert.Equal("maybe", (await svc.GetItemAsync("maybe"))?.Id);
        }

        [Fact]
        public async Task A_found_item_is_not_remembered_as_missing()
        {
            var (svc, p, _, _) = Make();
            p.Answer = Data;
            Assert.NotNull(await svc.GetItemAsync("here"));
            Assert.NotNull(await svc.GetItemAsync("here"));
        }

        [Fact]
        public async Task Continue_watching_looks_up_only_enough_to_fill_the_row_and_skips_the_dead_ones()
        {
            var (svc, p, h, _) = Make();
            var now = new DateTime(2026, 11, 11, 12, 0, 0);
            // 100 unfinished entries, newest first: the 30 newest are dead, the rest are fine.
            h.Items = Enumerable.Range(0, 100).Select(i => new HistoryItem { Id = $"id{i:000}", LastWatched = now.AddHours(-i), Duration = TimeSpan.FromHours(1), LastPosition = TimeSpan.FromMinutes(5) }).ToList();
            p.Answer = id => int.Parse(id[2..]) < 30 ? null : Data(id);

            var row = (await svc.GetContinueWatchingAsync()).ToList();

            Assert.Equal(DataService.ContinueWatchingMax, row.Count);
            Assert.Equal("id030", row.OrderByDescending(r => r.History?.LastWatched).First().Id);
            Assert.True(p.Total <= 30 + DataService.ContinueWatchingMax + 4, $"asked about {p.Total} ids");   // not all 100
            Assert.DoesNotContain("id099", p.Asked.Keys);

            var before = p.Total; await svc.GetContinueWatchingAsync();
            Assert.Equal(before, p.Total);                                    // second time: dead ones remembered, live ones cached
        }

        [Fact]
        public async Task Simultaneous_lookups_of_one_id_share_one_ask()
        {
            var (svc, p, _, _) = Make();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => svc.GetItemAsync("gone")));
            Assert.Equal(1, p.Asked["gone"]);
        }

        [Fact]
        public async Task Channels_saved_as_finished_by_the_old_rule_still_show_in_continue_watching_but_finished_videos_do_not()
        {
            var (svc, p, h, _) = Make();
            var now = new DateTime(2026, 11, 13, 22, 0, 0);
            h.Items = new List<HistoryItem>
            {
                new() { Id = "itv1", LastWatched = now, Duration = TimeSpan.Zero, LastPosition = TimeSpan.FromMinutes(40), IsFinished = true },   // a channel, wrongly "finished"
                new() { Id = "clip", LastWatched = now.AddMinutes(-5), Duration = TimeSpan.Zero, LastPosition = TimeSpan.FromMinutes(3), IsFinished = true }, // a video that ended
                new() { Id = "film", LastWatched = now.AddMinutes(-10), Duration = TimeSpan.FromHours(2), LastPosition = TimeSpan.FromHours(2), IsFinished = true },
            };
            p.Answer = id => id == "itv1" ? Data(id) with { IsLive = true, Type = MediaType.Channel } : Data(id);

            var row = (await svc.GetContinueWatchingAsync()).ToList();

            Assert.Equal(new[] { "itv1" }, row.Select(r => r.Id));
        }
    }
}
