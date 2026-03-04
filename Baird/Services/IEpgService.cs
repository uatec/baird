using System.Threading.Tasks;

namespace Baird.Services
{
    public interface IEpgService
    {
        Task<string?> GetCurrentProgrammeNameAsync(string channelId);
    }
}
