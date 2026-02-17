using System;
using System.Threading.Tasks;
using Baird.Services;

namespace Baird.Tests.Mocks
{
    public class MockCecService : ICecService
    {
        public event EventHandler<CecCommandLoggedEventArgs>? CommandLogged;
        
        public Task StartAsync() => Task.CompletedTask;
        public Task TogglePowerAsync() => Task.CompletedTask;
        public Task PowerOnAsync() => Task.CompletedTask;
        public Task PowerOffAsync() => Task.CompletedTask;
        public Task VolumeUpAsync() => Task.CompletedTask;
        public Task VolumeDownAsync() => Task.CompletedTask;
        public Task ChangeInputToThisDeviceAsync() => Task.CompletedTask;
        public Task CycleInputsAsync() => Task.CompletedTask;
        public Task<string> GetPowerStatusAsync() => Task.FromResult("Mock");
    }
}