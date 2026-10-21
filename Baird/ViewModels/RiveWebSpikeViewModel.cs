using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reactive;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;

namespace Baird.ViewModels
{
    public class RiveWebSpikeViewModel : ReactiveObject, IDisposable
    {
        private HttpListener? _httpListener;
        private CancellationTokenSource? _serverCts;
        private int _serverPort = 8088;
        private string _serverStatus = "Server Stopped";
        private bool _isServerRunning;
        private string _serverUrl = "http://127.0.0.1:8088/";
        private string _spikeDirectory = string.Empty;
        private bool _spikeDirectoryExists;
        private string _selectedRivAsset = "digital_music_player.riv";

        public event EventHandler? BackRequested;

        public ObservableCollection<string> AvailableRivAssets { get; } = new();

        public bool IsServerRunning
        {
            get => _isServerRunning;
            private set
            {
                this.RaiseAndSetIfChanged(ref _isServerRunning, value);
                this.RaisePropertyChanged(nameof(CanStartServer));
                this.RaisePropertyChanged(nameof(CanStopServer));
                this.RaisePropertyChanged(nameof(ServerStatusColor));
            }
        }

        public bool CanStartServer => !IsServerRunning;
        public bool CanStopServer => IsServerRunning;

        public string ServerStatusColor => IsServerRunning ? "#10B981" : "#94A3B8";

        public int ServerPort
        {
            get => _serverPort;
            set
            {
                this.RaiseAndSetIfChanged(ref _serverPort, value);
                ServerUrl = $"http://127.0.0.1:{value}/";
            }
        }

        public string ServerUrl
        {
            get => _serverUrl;
            private set => this.RaiseAndSetIfChanged(ref _serverUrl, value);
        }

        public string ServerStatus
        {
            get => _serverStatus;
            private set => this.RaiseAndSetIfChanged(ref _serverStatus, value);
        }

        public string SpikeDirectory
        {
            get => _spikeDirectory;
            private set
            {
                this.RaiseAndSetIfChanged(ref _spikeDirectory, value);
                SpikeDirectoryExists = Directory.Exists(value);
            }
        }

        public bool SpikeDirectoryExists
        {
            get => _spikeDirectoryExists;
            private set => this.RaiseAndSetIfChanged(ref _spikeDirectoryExists, value);
        }

        public string SelectedRivAsset
        {
            get => _selectedRivAsset;
            set => this.RaiseAndSetIfChanged(ref _selectedRivAsset, value);
        }

        public string WasmArchitectureSummary { get; } =
            "The official @rive-app/canvas WebAssembly runtime embeds Rive's C++ rendering engine into modern browsers. " +
            "It exposes high-level scene management and state machines directly in JavaScript while maintaining near-native " +
            "vector rasterization speeds. Memory overhead is strictly constrained (< 25MB WASM heap), and .riv binary assets " +
            "are typically 20KB-60KB, far smaller than raster sprite sheets or video clips.";

        public string WaylandPerformanceSummary { get; } =
            "Under Linux/Wayland (such as labwc or sway on Raspberry Pi 5), the browser renders the transparent Rive canvas into " +
            "a Wayland surface or subsurface (wl_subsurface). By utilizing zero-copy dmabuf hardware decoders for the video plane, " +
            "the compositor blends the vector UI onto the hardware-decoded stream at full 60fps refresh rates without tearing or A/V desync.";

        public string WidevineOverlaySummary { get; } =
            "For DRM streams (Netflix, BBC iPlayer, etc.), Widevine requires encrypted video presentation pipelines. " +
            "Running Rive in a transparent HTML5 canvas overlay positioned on top of the Widevine <video> element enables rich " +
            "interactive HUD animations (scrub bars, D-Pad focus indicators, audio meters) without violating DRM isolation or " +
            "sacrificing hardware-accelerated decoding.";

        public string ComparisonSummary { get; } =
            "• Rive Advantages: Fluid skeletal animations, state machine logic visually authored in Rive Editor, micro-sized vector assets, and seamless web embedding over Widevine.\n" +
            "• Avalonia Advantages: Direct C# access, deeply integrated into .NET runtime, built-in XAML layout system, and native Linux framebuffer/DRM support.\n" +
            "• Verdict: A hybrid architecture where Baird hosts a lightweight Web View overlay for streaming services with Rive UI delivers the best balance of 10-foot visual polish and DRM playback.";

        public ReactiveCommand<Unit, Unit> LaunchInBrowserCommand { get; }
        public ReactiveCommand<Unit, Unit> StartServerCommand { get; }
        public ReactiveCommand<Unit, Unit> StopServerCommand { get; }
        public ReactiveCommand<Unit, Unit> OpenSpikeDirectoryCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        public RiveWebSpikeViewModel()
        {
            LocateSpikeDirectory();

            AvailableRivAssets.Add("digital_music_player.riv (Baird HUD Media Player)");
            AvailableRivAssets.Add("rating.riv (Interactive Star Rating & Feedback)");
            AvailableRivAssets.Add("vehicles.riv (Vehicle Suspension Simulation)");

            LaunchInBrowserCommand = ReactiveCommand.CreateFromTask(LaunchInBrowserAsync);
            StartServerCommand = ReactiveCommand.Create(StartServer);
            StopServerCommand = ReactiveCommand.Create(StopServer);
            OpenSpikeDirectoryCommand = ReactiveCommand.Create(OpenSpikeDirectory);
            BackCommand = ReactiveCommand.Create(() => BackRequested?.Invoke(this, EventArgs.Empty));

            // Start server automatically so it's ready for instant launch
            StartServer();
        }

        private void LocateSpikeDirectory()
        {
            var baseDir = AppContext.BaseDirectory;
            var current = new DirectoryInfo(baseDir);

            // Traverse upwards to find repo root with 'spikes/rive-web-spike'
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "spikes", "rive-web-spike");
                if (Directory.Exists(candidate))
                {
                    SpikeDirectory = candidate;
                    return;
                }
                current = current.Parent;
            }

            // Fallback to relative path
            SpikeDirectory = Path.GetFullPath("spikes/rive-web-spike");
        }

        public void StartServer()
        {
            if (IsServerRunning) return;

            try
            {
                _serverPort = FindAvailablePort(8088);
                ServerUrl = $"http://127.0.0.1:{_serverPort}/";

                _httpListener = new HttpListener();
                _httpListener.Prefixes.Add($"http://127.0.0.1:{_serverPort}/");
                _httpListener.Start();

                _serverCts = new CancellationTokenSource();
                IsServerRunning = true;
                ServerStatus = $"Serving on {ServerUrl}";

                Task.Run(() => ListenLoopAsync(_serverCts.Token));
            }
            catch (Exception ex)
            {
                ServerStatus = $"Server Error: {ex.Message}";
                IsServerRunning = false;
            }
        }

        public void StopServer()
        {
            if (!IsServerRunning) return;

            try
            {
                _serverCts?.Cancel();
                _httpListener?.Stop();
                _httpListener?.Close();
            }
            catch
            {
                // Ignore cleanup errors
            }
            finally
            {
                _httpListener = null;
                _serverCts = null;
                IsServerRunning = false;
                ServerStatus = "Server Stopped";
            }
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _httpListener != null && _httpListener.IsListening)
            {
                try
                {
                    var context = await _httpListener.GetContextAsync();
                    _ = Task.Run(() => ProcessRequest(context), ct);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RiveWebSpikeServer] Error: {ex.Message}");
                }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            try
            {
                var reqPath = context.Request.Url?.AbsolutePath.TrimStart('/') ?? string.Empty;
                if (string.IsNullOrEmpty(reqPath))
                {
                    reqPath = "index.html";
                }

                var filePath = Path.Combine(SpikeDirectory, reqPath);

                // Prevent path traversal
                var fullFilePath = Path.GetFullPath(filePath);
                if (!fullFilePath.StartsWith(Path.GetFullPath(SpikeDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 403;
                    context.Response.Close();
                    return;
                }

                if (!File.Exists(fullFilePath))
                {
                    context.Response.StatusCode = 404;
                    using var writer = new StreamWriter(context.Response.OutputStream);
                    writer.Write($"404 Not Found: {reqPath}");
                    context.Response.Close();
                    return;
                }

                var ext = Path.GetExtension(fullFilePath).ToLowerInvariant();
                context.Response.ContentType = ext switch
                {
                    ".html" => "text/html; charset=utf-8",
                    ".js" => "application/javascript",
                    ".wasm" => "application/wasm",
                    ".riv" => "application/octet-stream",
                    ".css" => "text/css; charset=utf-8",
                    ".json" => "application/json",
                    ".png" => "image/png",
                    ".svg" => "image/svg+xml",
                    _ => "application/octet-stream"
                };

                context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                context.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");

                var fileBytes = File.ReadAllBytes(fullFilePath);
                context.Response.ContentLength64 = fileBytes.Length;
                context.Response.StatusCode = 200;
                context.Response.OutputStream.Write(fileBytes, 0, fileBytes.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                try
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                }
                catch { }
                Console.WriteLine($"[RiveWebSpikeServer] Request handling failed: {ex.Message}");
            }
        }

        public async Task LaunchInBrowserAsync()
        {
            if (!IsServerRunning)
            {
                StartServer();
                await Task.Delay(200);
            }

            try
            {
                OpenBrowser(ServerUrl);
                ServerStatus = $"Launched in browser: {ServerUrl}";
            }
            catch (Exception ex)
            {
                ServerStatus = $"Launch Failed: {ex.Message}";
            }
        }

        private void OpenBrowser(string url)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
        }

        private void OpenSpikeDirectory()
        {
            if (!Directory.Exists(SpikeDirectory)) return;

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start("xdg-open", SpikeDirectory);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start("explorer.exe", SpikeDirectory);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", SpikeDirectory);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RiveWebSpikeViewModel] Open directory failed: {ex.Message}");
            }
        }

        private static int FindAvailablePort(int startPort)
        {
            for (var port = startPort; port < startPort + 100; port++)
            {
                try
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                    return port;
                }
                catch (SocketException)
                {
                    continue;
                }
            }
            return startPort;
        }

        public void Dispose()
        {
            StopServer();
        }
    }
}
