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

    /// <summary>
    /// A key on the TV's own remote, passed on over CEC: User Control Pressed (with <see cref="Code"/>) or
    /// User Control Released (<see cref="Code"/> null). The TV sends them to whichever device it is showing.
    /// </summary>
    public class CecUserControlEventArgs : EventArgs
    {
        public int Source { get; init; }
        public int Destination { get; init; }
        /// <summary>Addressed to Baird's logical address, rather than another device on the bus.</summary>
        public bool ForThisDevice { get; init; }
        public byte? Code { get; init; }
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
        event EventHandler<CecUserControlEventArgs>? UserControlReceived;

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
