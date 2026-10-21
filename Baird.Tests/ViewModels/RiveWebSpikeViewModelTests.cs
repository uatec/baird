using System;
using System.IO;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Baird.ViewModels;
using Xunit;

namespace Baird.Tests.ViewModels
{
    public class RiveWebSpikeViewModelTests : IDisposable
    {
        private readonly RiveWebSpikeViewModel _vm;

        public RiveWebSpikeViewModelTests()
        {
            _vm = new RiveWebSpikeViewModel();
        }

        public void Dispose()
        {
            _vm.Dispose();
        }

        [Fact]
        public void InitialState_IsConfiguredProperly()
        {
            Assert.NotNull(_vm.AvailableRivAssets);
            Assert.Contains(_vm.AvailableRivAssets, a => a.Contains("digital_music_player.riv"));
            Assert.Contains(_vm.AvailableRivAssets, a => a.Contains("rating.riv"));
            Assert.Contains(_vm.AvailableRivAssets, a => a.Contains("vehicles.riv"));
            Assert.NotEmpty(_vm.WasmArchitectureSummary);
            Assert.NotEmpty(_vm.WaylandPerformanceSummary);
            Assert.NotEmpty(_vm.WidevineOverlaySummary);
            Assert.NotEmpty(_vm.ComparisonSummary);
            Assert.NotNull(_vm.SpikeDirectory);
            Assert.True(Directory.Exists(_vm.SpikeDirectory));
        }

        [Fact]
        public async Task EmbeddedServer_ServesSpikeFilesSuccessfully()
        {
            // Verify server started
            Assert.True(_vm.IsServerRunning);
            Assert.StartsWith("http://127.0.0.1:", _vm.ServerUrl);

            using var client = new HttpClient();
            var response = await client.GetAsync(_vm.ServerUrl);
            Assert.True(response.IsSuccessStatusCode);

            var content = await response.Content.ReadAsStringAsync();
            Assert.Contains("Baird - Spike 3: Rive Control in Web View", content);
            Assert.Contains("digital_music_player.riv", content);
        }

        [Fact]
        public async Task EmbeddedServer_ServesWasmAndRivFilesWithCorrectHeaders()
        {
            using var client = new HttpClient();

            // Request .wasm
            var wasmUrl = $"{_vm.ServerUrl}rive.wasm";
            var wasmResp = await client.GetAsync(wasmUrl);
            Assert.True(wasmResp.IsSuccessStatusCode);
            Assert.Equal("application/wasm", wasmResp.Content.Headers.ContentType?.MediaType);

            // Request .riv
            var rivUrl = $"{_vm.ServerUrl}assets/digital_music_player.riv";
            var rivResp = await client.GetAsync(rivUrl);
            Assert.True(rivResp.IsSuccessStatusCode);
            Assert.Equal("application/octet-stream", rivResp.Content.Headers.ContentType?.MediaType);
        }

        [Fact]
        public void StopAndStartServer_TogglesServerState()
        {
            _vm.StopServer();
            Assert.False(_vm.IsServerRunning);
            Assert.Equal("Server Stopped", _vm.ServerStatus);

            _vm.StartServer();
            Assert.True(_vm.IsServerRunning);
            Assert.StartsWith("Serving on http://127.0.0.1:", _vm.ServerStatus);
        }

        [Fact]
        public void BackCommand_FiresBackRequestedEvent()
        {
            bool backFired = false;
            _vm.BackRequested += (s, e) => backFired = true;

            _vm.BackCommand.Execute().Subscribe();
            Assert.True(backFired);
        }
    }
}
