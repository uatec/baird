using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baird.Models;
using Baird.Services;

namespace Baird.Tests.Mocks
{
    public class MockMediaProvider : IMediaProvider
    {
        public string Name { get; }
        private readonly List<MediaItemData> _items;

        public MockMediaProvider(string name, IEnumerable<MediaItemData> items)
        {
            Name = name;
            _items = items.ToList();
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task<IEnumerable<MediaItemData>> GetListingAsync()
        {
            return Task.FromResult((IEnumerable<MediaItemData>)_items);
        }

        public Task<IEnumerable<MediaItemData>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default)
        {
            // Simple mock search: contains name or channel number
            if (string.IsNullOrWhiteSpace(query))
            {
                return Task.FromResult((IEnumerable<MediaItemData>)_items);
            }

            var results = _items.Where(i =>
                (i.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (i.ChannelNumber?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
            );

            return Task.FromResult(results);
        }

        public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id)
        {
            return Task.FromResult(Enumerable.Empty<MediaItemData>());
        }

        public Task<MediaItemData?> GetItemAsync(string id)
        {
            return Task.FromResult(_items.FirstOrDefault(x => x.Id == id));
        }
    }
}
