using System;
using System.Threading.Tasks;

namespace Baird.Services
{
    public class CecCommandLoggedEventArgs : EventArgs
    {
        public string Command { get; set; } = string.Empty;
        public string Response { get; set; } = string.Empty;
        public bool Success { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public interface ICecService
    {
        event EventHandler<CecCommandLoggedEventArgs>? CommandLogged;
        
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
