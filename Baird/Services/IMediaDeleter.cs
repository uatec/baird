using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>A provider that can delete an item and its files from the server (Jellyfin).</summary>
    public interface IMediaDeleter
    {
        /// <summary>True when it's gone (or was already).</summary>
        Task<bool> DeleteItemAsync(string itemId, CancellationToken cancellationToken = default);
    }
}
