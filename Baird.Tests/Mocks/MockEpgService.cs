using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Baird.Models;
using Baird.Services;

namespace Baird.Tests.Mocks
{
    public class MockEpgService : IEpgService
    {
        public Dictionary<string, List<EpgProgram>> MockSchedules { get; } = new();

        public Task<string?> GetCurrentProgrammeNameAsync(string channelId)
        {
            if (MockSchedules.TryGetValue(channelId, out var list) && list.Count > 0)
            {
                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var current = list.FirstOrDefault(p => p.StartUnix <= nowUnix && nowUnix < p.StopUnix) ?? list[0];
                return Task.FromResult<string?>(current.Title);
            }
            return Task.FromResult<string?>(null);
        }

        public Task<EpgProgram?> GetCurrentProgrammeAsync(string channelId)
        {
            if (MockSchedules.TryGetValue(channelId, out var list) && list.Count > 0)
            {
                var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var current = list.FirstOrDefault(p => p.StartUnix <= nowUnix && nowUnix < p.StopUnix) ?? list[0];
                return Task.FromResult<EpgProgram?>(current);
            }
            return Task.FromResult<EpgProgram?>(null);
        }

        public Task<IReadOnlyList<EpgProgram>> GetChannelScheduleAsync(string channelId, int limit = 20)
        {
            if (MockSchedules.TryGetValue(channelId, out var list))
            {
                return Task.FromResult<IReadOnlyList<EpgProgram>>(list.Take(limit).ToList());
            }
            return Task.FromResult<IReadOnlyList<EpgProgram>>(Array.Empty<EpgProgram>());
        }

        public Task<IReadOnlyDictionary<string, IReadOnlyList<EpgProgram>>> GetBulkScheduleAsync(
            IEnumerable<string> channelIds,
            int limitPerChannel = 10,
            CancellationToken cancellationToken = default)
        {
            var dict = new Dictionary<string, IReadOnlyList<EpgProgram>>();
            foreach (var id in channelIds)
            {
                if (MockSchedules.TryGetValue(id, out var list))
                {
                    dict[id] = list.Take(limitPerChannel).ToList();
                }
                else
                {
                    dict[id] = Array.Empty<EpgProgram>();
                }
            }
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<EpgProgram>>>(dict);
        }
    }
}
