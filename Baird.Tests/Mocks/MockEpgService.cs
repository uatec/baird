using System.Threading.Tasks;
using Baird.Services;

namespace Baird.Tests.Mocks
{
    public class MockEpgService : IEpgService
    {
        public Task<string?> GetCurrentProgrammeNameAsync(string channelId) => Task.FromResult<string?>(null);
    }
}
