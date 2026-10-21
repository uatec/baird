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

    public enum TvPowerStatus
    {
        Unknown,
        On,
        Standby,
        TransitioningToOn,
        TransitioningToStandby,
    }

    public interface ICecService
    {
        event EventHandler<CecCommandLoggedEventArgs>? CommandLogged;
        event EventHandler? TvStandby;
        event EventHandler? TvPowerOn;
        event EventHandler? InputLost;
        event EventHandler? InputRegained;

        bool IsAvailable { get; }

        Task StartAsync();
        /// <summary>Queries the TV and switches it to the opposite state. Returns true if it was turned on.</summary>
        Task<bool> TogglePowerAsync();
        Task PowerOnAsync();
        Task PowerOffAsync();
        Task VolumeUpAsync();
        Task VolumeDownAsync();
        Task MuteAsync();
        Task ChangeInputToThisDeviceAsync();
        Task CycleInputsAsync();
        Task<TvPowerStatus> GetPowerStatusAsync();
    }
}
