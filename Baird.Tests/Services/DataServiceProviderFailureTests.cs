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
    /// One provider failing (e.g. Jellyfin returning 500/503 at startup) must not wipe out the
    /// results of the others — that blanked the TvHeadend channel list so nothing auto-played.
    /// </summary>
    public class DataServiceProviderFailureTests
    {
        private class ThrowingProvider : IMediaProvider
        {
            public string Name => "Broken";
            public Task<IEnumerable<MediaItemData>> GetListingAsync() => throw new InvalidOperationException("503 (Service Unavailable)");
            public Task<IEnumerable<MediaItemData>> SearchAsync(string query, CancellationToken cancellationToken = default) => Task.FromException<IEnumerable<MediaItemData>>(new InvalidOperationException("500"));
            public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id) => Task.FromException<IEnumerable<MediaItemData>>(new InvalidOperationException("500"));
            public Task<MediaItemData?> GetItemAsync(string id) => Task.FromResult<MediaItemData?>(null);
        }

        private class NoHistory : IHistoryService
        {
            public Task UpsertAsync(MediaItemViewModel media, TimeSpan position, TimeSpan duration) => Task.CompletedTask;
            public Task<List<HistoryItem>> GetHistoryAsync() => Task.FromResult(new List<HistoryItem>());
            public HistoryItem? GetProgress(string id) => null;
            public Task ClearHistoryAsync() => Task.CompletedTask;
        }

        private class NoWatchlist : IWatchlistService
        {
#pragma warning disable CS0067 // required by the interface; never raised in these tests
            public event EventHandler? WatchlistUpdated;
#pragma warning restore CS0067
            public Task AddAsync(string id) => Task.CompletedTask;
            public Task RemoveAsync(string id) => Task.CompletedTask;
            public Task<HashSet<string>> GetWatchlistIdsAsync() => Task.FromResult(new HashSet<string>());
            public bool IsOnWatchlist(string id) => false;
        }

        private static MediaItemData Channel(string id, string name) => new()
        {
            Id = id,
            Name = name,
            Source = "TvHeadend",
            Type = MediaType.Channel,
            Details = "",
            ImageUrl = "",
            IsLive = true,
            Synopsis = "",
            Subtitle = "",
        };

        private static DataService CreateService()
        {
            var tv = new Baird.Tests.Mocks.MockMediaProvider("TvHeadend", new[] { Channel("c1", "BBC One HD"), Channel("c2", "BBC Two HD") });
            return new DataService(new IMediaProvider[] { tv, new ThrowingProvider() }, new NoHistory(), new NoWatchlist(),
                new MediaItemCache(), new Baird.Tests.Mocks.MockMediaDataCache());
        }

        [Fact]
        public async Task GetListingAsync_OneProviderThrows_ReturnsOtherProvidersItems()
        {
            var items = (await CreateService().GetListingAsync()).ToList();

            Assert.Equal(new[] { "c1", "c2" }, items.Select(i => i.Id).OrderBy(x => x));
        }

        [Fact]
        public async Task SearchAsync_OneProviderFaults_ReturnsOtherProvidersItems()
        {
            var items = (await CreateService().SearchAsync("BBC")).ToList();

            Assert.Equal(2, items.Count);
        }

        [Fact]
        public async Task GetChildrenAsync_OneProviderFaults_DoesNotThrow()
        {
            var items = await CreateService().GetChildrenAsync("anything");

            Assert.NotNull(items);
        }
    }
}
