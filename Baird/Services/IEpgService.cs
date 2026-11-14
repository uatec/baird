using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;

namespace Baird.Services
{
    public interface IEpgService
    {
        Task<string?> GetCurrentProgrammeNameAsync(string channelId);
        Task<EpgProgram?> GetCurrentProgrammeAsync(string channelId);
        Task<IReadOnlyList<EpgProgram>> GetChannelScheduleAsync(string channelId, int limit = 20);
        Task<IReadOnlyDictionary<string, IReadOnlyList<EpgProgram>>> GetBulkScheduleAsync(
            IEnumerable<string> channelIds,
            int limitPerChannel = 10,
            CancellationToken cancellationToken = default);

        /// <summary>Ask the tuner to refresh the guide over the air now, if nothing is using it. Never throws; throttled.</summary>
        void RequestGrab() { }
    }
}
