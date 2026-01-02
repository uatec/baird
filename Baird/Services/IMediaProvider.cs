using System.Collections.Generic;
using System.Threading.Tasks;

namespace Baird.Services
{
    public interface IMediaProvider
    {
        Task InitializeAsync();
        Task<IEnumerable<MediaItem>> GetListingAsync();
        string GetStreamUrl(string itemId);
    }
}
