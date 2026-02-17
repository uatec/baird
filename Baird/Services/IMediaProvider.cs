using System.Collections.Generic;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    public interface IMediaProvider
    {
        string Name { get; }
        Task<IEnumerable<MediaItemData>> GetListingAsync();
        Task<IEnumerable<MediaItemData>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default);
        Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id);
        Task<MediaItemData?> GetItemAsync(string id);
    }
}
