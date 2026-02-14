using Baird.Services;

namespace Baird.Tests.Mocks;

public class MockMediaProvider : IMediaProvider
{
    public string Name { get; }
    private readonly List<MediaItem> _items;

    public MockMediaProvider(string name, IEnumerable<MediaItem> items)
    {
        Name = name;
        _items = items.ToList();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task<IEnumerable<MediaItem>> GetListingAsync()
    {
        return Task.FromResult((IEnumerable<MediaItem>)_items);
    }

    public Task<IEnumerable<MediaItem>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default)
    {
        // Simple mock search: contains name or channel number
        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult((IEnumerable<MediaItem>)_items);
        }

        IEnumerable<MediaItem> results = _items.Where(i =>
            (i.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (i.ChannelNumber?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
        );

        return Task.FromResult(results);
    }

    public Task<IEnumerable<MediaItem>> GetChildrenAsync(string id)
    {
        return Task.FromResult(Enumerable.Empty<MediaItem>());
    }

    public Task<MediaItem?> GetItemAsync(string id)
    {
        return Task.FromResult(_items.FirstOrDefault(x => x.Id == id));
    }
}
