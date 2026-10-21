using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    public record VideoPlaybackStatus(
        bool Found,
        double CurrentTime,
        int ReadyState,
        bool Paused,
        double Duration,
        string Url,
        string Title);

    public record AuthPageStatus(
        string Url,
        string Title,
        bool HasLoginInput,
        bool HasMfaInput,
        bool IsVideo,
        double CurrentTime)
    {
        public NetflixPageKind Kind =>
            NetflixPageClassifier.Classify(Url, HasLoginInput, HasMfaInput, IsVideo, CurrentTime);
    }

    /// <summary>Result of asking the launched browser for a Widevine key system. A fact about EME access only, not proof of playback.</summary>
    public record WidevineStatus(bool Available, string Detail);

    public interface ICdpBrowserService : IAsyncDisposable
    {
        bool IsRunning { get; }
        string? BrowserPath { get; }
        int ProcessId { get; }
        /// <summary>Human-readable reason the last LaunchAsync returned false.</summary>
        string? LastError { get; }

        string FindBrowserExecutable();
        Task<bool> LaunchAsync(string browserPath, string url, int port, string userDataDir, bool visible = false, CancellationToken ct = default);
        Task<AuthPageStatus?> CheckAuthStatusAsync(CancellationToken ct = default);
        Task<WidevineStatus> CheckWidevineAsync(CancellationToken ct = default);
        Task<bool> SubmitMfaPinAsync(string pin, CancellationToken ct = default);
        Task<VideoPlaybackStatus?> PollVideoPlaybackAsync(CancellationToken ct = default);
        Task<bool> InjectOverlayScriptAsync(string? script = null, CancellationToken ct = default);
        Task<bool> RevealWindowAsync(CancellationToken ct = default);
        Task StopAsync();
    }

    public class CdpBrowserService : ICdpBrowserService
    {
        private Process? _browserProcess;
        private ClientWebSocket? _pageWs;
        private ClientWebSocket? _browserWs;
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingPageRequests = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingBrowserRequests = new();
        private CancellationTokenSource? _recvCts;
        private int _pageReqId;
        private int _browserReqId;
        private int _cdpPort = 9222;

        public bool IsRunning => _browserProcess != null && !_browserProcess.HasExited;
        public string? BrowserPath { get; private set; }
        public int ProcessId => _browserProcess?.Id ?? 0;
        public string? LastError { get; private set; }

        private bool Fail(string message)
        {
            LastError = message;
            Log("[CDP] " + message);
            return false;
        }

        public event Action<string>? LogMessage;

        private void Log(string msg) => LogMessage?.Invoke(msg);

        public string FindBrowserExecutable()
        {
            var envBin = Environment.GetEnvironmentVariable("CHROME_BIN") 
                         ?? Environment.GetEnvironmentVariable("CHROMIUM_BIN");
            if (!string.IsNullOrEmpty(envBin) && File.Exists(envBin))
            {
                return envBin;
            }

            string[] candidates =
            {
                "/usr/bin/google-chrome-stable",
                "/usr/bin/google-chrome",
                "/usr/bin/chromium",
                "/usr/bin/chromium-browser",
                "/usr/bin/brave-browser",
                "/snap/bin/chromium",
                "/var/lib/flatpak/exports/bin/org.chromium.Chromium"
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            // Fallback: check PATH via 'which'
            try
            {
                foreach (var name in new[] { "google-chrome-stable", "google-chrome", "chromium", "chromium-browser" })
                {
                    using var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "which",
                        Arguments = name,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    if (proc != null)
                    {
                        var output = proc.StandardOutput.ReadToEnd().Trim();
                        proc.WaitForExit();
                        if (!string.IsNullOrEmpty(output) && File.Exists(output))
                        {
                            return output;
                        }
                    }
                }
            }
            catch { }

            return candidates[0]; // Default fallback
        }

        public async Task<bool> LaunchAsync(string browserPath, string url, int port, string userDataDir, bool visible = false, CancellationToken ct = default)
        {
            LastError = null;
            await StopAsync();

            _cdpPort = port;
            BrowserPath = browserPath;

            Directory.CreateDirectory(userDataDir);

            Log($"[CDP] Starting browser process: {browserPath}");
            Log($"[CDP] URL: {url}");
            Log($"[CDP] User data dir: {userDataDir}");
            Log(visible ? "[CDP] Mode: VISIBLE (sign-in)" : "[CDP] Mode: hidden, window at -4000,-4000");

            var psi = new ProcessStartInfo
            {
                FileName = browserPath,
                Arguments = $"--remote-debugging-port={port} " +
                            (visible ? "" : "--window-position=-4000,-4000 ") +
                            $"--window-size=1920,1080 " +
                            $"--user-data-dir=\"{userDataDir}\" " +
                            $"--autoplay-policy=no-user-gesture-required " +
                            $"--no-first-run " +
                            $"--no-default-browser-check " +
                            $"--password-store=basic " +
                            $"--disable-session-crashed-bubble " +
                            $"--class=baird-netflix " +
                            // Test hook only (e.g. "--headless=new" on a box without a display).
                            (Environment.GetEnvironmentVariable("BAIRD_CHROME_EXTRA_ARGS") is { Length: > 0 } extra ? extra + " " : "") +
                            $"\"{url}\"",
                UseShellExecute = false,
                CreateNoWindow = false
            };

            try
            {
                _browserProcess = Process.Start(psi);
                if (_browserProcess == null)
                {
                    return Fail("Failed to start browser process.");
                }
                Log($"[CDP] Browser process started (PID: {_browserProcess.Id}). Waiting for CDP port {port}...");
            }
            catch (Exception ex)
            {
                return Fail($"Could not start '{browserPath}': {ex.Message}");
            }

            // Poll for CDP endpoint readiness
            string? browserWsUrl = null;
            string? pageWsUrl = null;
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < TimeSpan.FromSeconds(15) && !ct.IsCancellationRequested)
            {
                if (_browserProcess.HasExited)
                {
                    return Fail($"Browser exited with code {_browserProcess.ExitCode} before CDP came up (is another instance using the same profile?).");
                }

                try
                {
                    // 1. Check version endpoint for browser websocket
                    if (browserWsUrl == null)
                    {
                        var versionResp = await _http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct);
                        using var vDoc = JsonDocument.Parse(versionResp);
                        if (vDoc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var bwProp))
                        {
                            browserWsUrl = bwProp.GetString();
                            Log($"[CDP] Found browser WebSocket: {browserWsUrl}");
                        }
                    }

                    // 2. Check list endpoint for page websocket
                    var listResp = await _http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct);
                    using var lDoc = JsonDocument.Parse(listResp);
                    foreach (var item in lDoc.RootElement.EnumerateArray())
                    {
                        var type = item.TryGetProperty("type", out var tProp) ? tProp.GetString() : null;
                        if (type == "page" && item.TryGetProperty("webSocketDebuggerUrl", out var pwProp))
                        {
                            pageWsUrl = pwProp.GetString();
                            Log($"[CDP] Found page WebSocket: {pageWsUrl}");
                            break;
                        }
                    }

                    if (pageWsUrl != null)
                    {
                        break;
                    }
                }
                catch
                {
                    // Connection refused or not yet ready; retry
                }

                await Task.Delay(250, ct);
            }

            if (pageWsUrl == null)
            {
                return Fail($"Timed out after 15s waiting for the DevTools endpoint on port {port}.");
            }

            // Connect WebSockets
            _recvCts = new CancellationTokenSource();

            try
            {
                _pageWs = new ClientWebSocket();
                await _pageWs.ConnectAsync(new Uri(pageWsUrl), ct);
                _ = Task.Run(() => ReceiveLoopAsync(_pageWs, _pendingPageRequests, _recvCts.Token));
                Log("[CDP] Connected to Page DevTools WebSocket.");

                if (browserWsUrl != null)
                {
                    _browserWs = new ClientWebSocket();
                    await _browserWs.ConnectAsync(new Uri(browserWsUrl), ct);
                    _ = Task.Run(() => ReceiveLoopAsync(_browserWs, _pendingBrowserRequests, _recvCts.Token));
                    Log("[CDP] Connected to Browser DevTools WebSocket.");
                }

                return true;
            }
            catch (Exception ex)
            {
                return Fail($"WebSocket connection error: {ex.Message}");
            }
        }

        public async Task<AuthPageStatus?> CheckAuthStatusAsync(CancellationToken ct = default)
        {
            const string script = @"(function() {
                const hasLoginInput = !!document.querySelector('input[name=""userLoginId""], input[name=""password""], input[type=""password""]');
                const hasMfaInput = !!document.querySelector('input[data-uia*=""pin""], input[name*=""pin""], input[autocomplete=""one-time-code""], .mfa-input');
                const video = document.querySelector('video');
                return {
                    url: window.location.href,
                    title: document.title || '',
                    hasLoginInput: hasLoginInput,
                    hasMfaInput: hasMfaInput,
                    isVideo: !!video,
                    currentTime: video ? video.currentTime : 0
                };
            })()";

            var result = await EvaluateInPageAsync(script, ct);
            if (result.HasValue && result.Value.ValueKind == JsonValueKind.Object)
            {
                var r = result.Value;
                return new AuthPageStatus(
                    Url: r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    Title: r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    HasLoginInput: r.TryGetProperty("hasLoginInput", out var l) && l.GetBoolean(),
                    HasMfaInput: r.TryGetProperty("hasMfaInput", out var m) && m.GetBoolean(),
                    IsVideo: r.TryGetProperty("isVideo", out var v) && v.GetBoolean(),
                    CurrentTime: r.TryGetProperty("currentTime", out var c) ? c.GetDouble() : 0
                );
            }

            return null;
        }

        public async Task<WidevineStatus> CheckWidevineAsync(CancellationToken ct = default)
        {
            const string script = @"(async function() {
                if (!navigator.requestMediaKeySystemAccess) return { available: false, detail: 'navigator.requestMediaKeySystemAccess missing (insecure context?)' };
                try {
                    const a = await navigator.requestMediaKeySystemAccess('com.widevine.alpha', [{
                        initDataTypes: ['cenc'],
                        videoCapabilities: [{ contentType: 'video/mp4; codecs=""avc1.42E01E""' }],
                        audioCapabilities: [{ contentType: 'audio/mp4; codecs=""mp4a.40.2""' }]
                    }]);
                    return { available: true, detail: 'requestMediaKeySystemAccess resolved for ' + a.keySystem };
                } catch (e) {
                    return { available: false, detail: String(e) };
                }
            })()";

            try
            {
                var result = await EvaluateInPageAsync(script, ct);
                if (result.HasValue && result.Value.ValueKind == JsonValueKind.Object)
                {
                    var r = result.Value;
                    return new WidevineStatus(
                        r.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True,
                        r.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "");
                }
                return new WidevineStatus(false, "No result from page (CDP not connected or script error).");
            }
            catch (Exception ex)
            {
                return new WidevineStatus(false, "CDP error: " + ex.Message);
            }
        }

        public async Task<bool> SubmitMfaPinAsync(string pin, CancellationToken ct = default)
        {
            var escapedPin = JsonEncodedText.Encode(pin).ToString();
            var script = $@"(function(pin) {{
                const input = document.querySelector('input[data-uia*=""pin""], input[name*=""code""], input[name*=""pin""], input[autocomplete=""one-time-code""]') ||
                              document.querySelector('input[type=""tel""], input[type=""number""], input[type=""text""]');
                if (input) {{
                    input.value = pin;
                    input.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    input.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    const form = input.closest('form');
                    const btn = form ? form.querySelector('button[type=""submit""]') : document.querySelector('button[data-uia*=""submit""], button[type=""submit""]');
                    if (btn) btn.click();
                    else if (form) form.submit();
                    return true;
                }}
                return false;
            }})(""{escapedPin}"")";

            Log($"[CDP] Submitting MFA PIN ({pin.Length} chars) to page input fields.");
            var result = await EvaluateInPageAsync(script, ct);
            return result.HasValue && result.Value.ValueKind == JsonValueKind.True;
        }

        public async Task<VideoPlaybackStatus?> PollVideoPlaybackAsync(CancellationToken ct = default)
        {
            const string script = @"(function() {
                const v = document.querySelector('video');
                if (!v) {
                    return {
                        found: false,
                        currentTime: 0,
                        readyState: 0,
                        paused: true,
                        duration: 0,
                        url: window.location.href,
                        title: document.title
                    };
                }
                return {
                    found: true,
                    currentTime: v.currentTime,
                    readyState: v.readyState,
                    paused: v.paused,
                    duration: v.duration,
                    url: window.location.href,
                    title: document.title
                };
            })()";

            var result = await EvaluateInPageAsync(script, ct);
            if (result.HasValue && result.Value.ValueKind == JsonValueKind.Object)
            {
                var r = result.Value;
                return new VideoPlaybackStatus(
                    Found: r.TryGetProperty("found", out var f) && f.GetBoolean(),
                    CurrentTime: r.TryGetProperty("currentTime", out var c) ? c.GetDouble() : 0,
                    ReadyState: r.TryGetProperty("readyState", out var s) ? s.GetInt32() : 0,
                    Paused: r.TryGetProperty("paused", out var p) && p.GetBoolean(),
                    Duration: r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0, // NaN/Infinity serialise to null
                    Url: r.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    Title: r.TryGetProperty("title", out var t) ? t.GetString() ?? "" : ""
                );
            }

            return null;
        }

        public async Task<bool> InjectOverlayScriptAsync(string? script = null, CancellationToken ct = default)
        {
            var code = script ?? LoadDefaultOverlayScript();
            Log($"[CDP] Injecting Baird 10-foot HUD script ({code.Length} bytes)...");
            var res = await EvaluateInPageAsync(code, ct);
            return res != null;
        }

        public static string LoadDefaultOverlayScript()
        {
            string[] candidatePaths =
            {
                Path.Combine(Directory.GetCurrentDirectory(), "spikes", "baird-netflix-poc", "baird-overlay.js"),
                Path.Combine(Directory.GetCurrentDirectory(), "Baird", "Resources", "baird-overlay.js"),
                Path.Combine(AppContext.BaseDirectory, "spikes", "baird-netflix-poc", "baird-overlay.js"),
                Path.Combine(AppContext.BaseDirectory, "Resources", "baird-overlay.js")
            };

            foreach (var path in candidatePaths)
            {
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }
            }

            // Fallback to EmbeddedResource
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("Baird.Resources.baird-overlay.js");
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
            }
            catch { }

            return "console.log('[Baird] Overlay script not found.');";
        }

        public async Task<bool> RevealWindowAsync(CancellationToken ct = default)
        {
            Log("[CDP] Revealing browser window: moving to (0,0) and requesting fullscreen...");

            // 1. Attempt CDP Browser.setWindowBounds
            if (_browserWs != null && _browserWs.State == WebSocketState.Open)
            {
                try
                {
                    var windowForTarget = await SendBrowserCommandAsync("Browser.getWindowForTarget", new { }, ct);
                    if (windowForTarget.TryGetProperty("windowId", out var widProp))
                    {
                        var windowId = widProp.GetInt32();
                        await SendBrowserCommandAsync("Browser.setWindowBounds", new
                        {
                            windowId = windowId,
                            bounds = new
                            {
                                left = 0,
                                top = 0,
                                width = 1920,
                                height = 1080,
                                windowState = "fullscreen"
                            }
                        }, ct);
                        Log($"[CDP] Set window bounds via CDP (windowId: {windowId}) to (0,0, 1920x1080, fullscreen).");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[CDP] Browser.setWindowBounds warning: {ex.Message}");
                }
            }

            // 2. Attempt Linux wmctrl & xdotool
            RunShellCommand("wmctrl", "-x -r baird-netflix -e 0,0,0,1920,1080");
            RunShellCommand("wmctrl", "-x -r baird-netflix -b add,fullscreen");
            RunShellCommand("xdotool", "search --class baird-netflix windowmove 0 0 windowactivate");

            // 3. Request fullscreen in page context
            try
            {
                await EvaluateInPageAsync(@"if (!document.fullscreenElement && document.documentElement.requestFullscreen) {
                    document.documentElement.requestFullscreen().catch(() => {});
                }", ct);
            }
            catch { }

            return true;
        }

        private static void RunShellCommand(string fileName, string args)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                proc?.WaitForExit(1000);
            }
            catch { }
        }

        private async Task<JsonElement?> EvaluateInPageAsync(string script, CancellationToken ct = default)
        {
            if (_pageWs == null || _pageWs.State != WebSocketState.Open) return null;

            var result = await SendPageCommandAsync("Runtime.evaluate", new
            {
                expression = script,
                returnByValue = true,
                awaitPromise = true
            }, ct);

            if (result.TryGetProperty("exceptionDetails", out var exc))
            {
                Log($"[CDP] Page script threw: {exc.GetRawText()}");
                return null;
            }

            if (result.TryGetProperty("result", out var resObj))
            {
                if (resObj.TryGetProperty("value", out var val))
                {
                    return val;
                }
                return resObj;
            }

            return result;
        }

        private async Task<JsonElement> SendPageCommandAsync(string method, object @params, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _pageReqId);
            return await SendCommandAsync(_pageWs!, _pendingPageRequests, id, method, @params, ct);
        }

        private async Task<JsonElement> SendBrowserCommandAsync(string method, object @params, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _browserReqId);
            return await SendCommandAsync(_browserWs!, _pendingBrowserRequests, id, method, @params, ct);
        }

        private static async Task<JsonElement> SendCommandAsync(
            ClientWebSocket ws,
            ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending,
            int id,
            string method,
            object @params,
            CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;

            using var reg = ct.Register(() =>
            {
                if (pending.TryRemove(id, out var p))
                {
                    p.TrySetCanceled(ct);
                }
            });

            var payload = JsonSerializer.Serialize(new
            {
                id = id,
                method = method,
                @params = @params
            });

            var bytes = Encoding.UTF8.GetBytes(payload);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);

            return await tcs.Task;
        }

        private static async Task ReceiveLoopAsync(
            ClientWebSocket ws,
            ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending,
            CancellationToken ct)
        {
            var buffer = new byte[65536];
            using var ms = new MemoryStream();

            try
            {
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    var bytes = ms.ToArray();
                    using var doc = JsonDocument.Parse(bytes);
                    var root = doc.RootElement.Clone();

                    if (root.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id))
                    {
                        if (pending.TryRemove(id, out var tcs))
                        {
                            if (root.TryGetProperty("error", out var errProp))
                            {
                                tcs.TrySetException(new Exception($"CDP Error: {errProp.GetRawText()}"));
                            }
                            else if (root.TryGetProperty("result", out var resProp))
                            {
                                tcs.TrySetResult(resProp);
                            }
                            else
                            {
                                tcs.TrySetResult(root);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                foreach (var kvp in pending)
                {
                    kvp.Value.TrySetCanceled();
                }
                pending.Clear();
            }
        }

        public async Task StopAsync()
        {
            // Ask Chrome to close itself first so cookies/profile are flushed to disk (needed for a sign-in to persist).
            if (_browserWs != null && _browserWs.State == WebSocketState.Open && IsRunning)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await SendBrowserCommandAsync("Browser.close", new { }, closeCts.Token);
                }
                catch { }
                try { _browserProcess?.WaitForExit(4000); } catch { }
            }

            _recvCts?.Cancel();
            _recvCts?.Dispose();
            _recvCts = null;

            if (_pageWs != null)
            {
                if (_pageWs.State == WebSocketState.Open)
                {
                    try
                    {
                        await _pageWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stop", CancellationToken.None);
                    }
                    catch { }
                }
                _pageWs.Dispose();
                _pageWs = null;
            }

            if (_browserWs != null)
            {
                if (_browserWs.State == WebSocketState.Open)
                {
                    try
                    {
                        await _browserWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stop", CancellationToken.None);
                    }
                    catch { }
                }
                _browserWs.Dispose();
                _browserWs = null;
            }

            if (_browserProcess != null)
            {
                try
                {
                    if (!_browserProcess.HasExited)
                    {
                        Log($"[CDP] Terminating browser process (PID: {_browserProcess.Id})...");
                        _browserProcess.Kill(entireProcessTree: true);
                        _browserProcess.WaitForExit(3000);
                    }
                }
                catch { }
                finally
                {
                    _browserProcess.Dispose();
                    _browserProcess = null;
                }
            }

            Log("[CDP] Browser session stopped cleanly.");
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _http.Dispose();
        }
    }
}
