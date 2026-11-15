using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    /// <summary>A provider that can list everything it has (Jellyfin): its films and series, newest added first.</summary>
    public interface IMediaLibrary
    {
        Task<IEnumerable<MediaItemData>> GetNewestAsync(CancellationToken cancellationToken = default);
    }
}
