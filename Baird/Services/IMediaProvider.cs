using System.Collections.Generic;
using System.Threading.Tasks;

namespace Baird.Services
{
    public interface IMediaProvider
    {
        Task InitializeAsync();
        Task<IEnumerable<MediaItem>> GetListingAsync();
        Task<IEnumerable<MediaItem>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default);
        Task<IEnumerable<MediaItem>> GetChildrenAsync(string id);
    }
}
