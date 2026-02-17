using System.Threading.Tasks;

namespace Baird.Services
{
    public interface ICecService
    {
        Task StartAsync();
        Task TogglePowerAsync();
        Task PowerOnAsync();
        Task PowerOffAsync();
        Task VolumeUpAsync();
        Task VolumeDownAsync();
        Task ChangeInputToThisDeviceAsync();
        Task CycleInputsAsync();
        Task<string> GetPowerStatusAsync();
    }
}
