namespace Baird.Services;

public interface IMediaProvider
{
    Task<IEnumerable<MediaItem>> GetListingAsync();
    Task<IEnumerable<MediaItem>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default);
    Task<IEnumerable<MediaItem>> GetChildrenAsync(string id);
    Task<MediaItem?> GetItemAsync(string id);
}
