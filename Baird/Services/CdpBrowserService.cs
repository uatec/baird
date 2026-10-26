using System;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        string Title,
        bool Ended = false,
        string? Prompt = null,
        double? TitleSeconds = null,
        double? TitleLengthSeconds = null);

    public record AuthPageStatus(
        string Url,
        string Title,
        bool HasLoginInput,
        bool HasMfaInput,
        bool IsVideo,
        double CurrentTime,
        string? MembershipStatus = null,
        bool HasSignInLink = false,
        string? PlaybackErrorCode = null,
        string? PlaybackErrorMessage = null)
    {
        /// <summary>Netflix itself reports a member (not ANONYMOUS). Used to confirm a sign-in worked.</summary>
        public bool IsSignedIn => MembershipStatus is { Length: > 0 } m && !string.Equals(m, "ANONYMOUS", StringComparison.OrdinalIgnoreCase);

        public NetflixPageKind Kind =>
            NetflixPageClassifier.Classify(Url, HasLoginInput, HasMfaInput, IsVideo, CurrentTime, MembershipStatus, HasSignInLink);
    }

    /// <summary>Result of asking the launched browser for a Widevine key system. A fact about EME access only, not proof of playback.</summary>
    public record WidevineStatus(bool Available, string Detail);

    /// <summary>Outcome of typing credentials into the login form. Detail never contains the credentials.</summary>
    public record SignInSubmitResult(bool Submitted, string Detail);

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
        Task<string> ClearSiteDataAsync(string origin, CancellationToken ct = default);
        Task<bool> NavigateAsync(string url, CancellationToken ct = default);
        Task<NetflixSessionState?> ReadNetflixSessionStateAsync(CancellationToken ct = default);
        /// <summary>
        /// <see cref="ReadNetflixSessionStateAsync"/> while the viewer signs in by hand on the raised page, plus whether they pressed
        /// Back (Escape) to give up.
        /// </summary>
        Task<NetflixSessionState?> WatchNetflixSignInAsync(CancellationToken ct = default);
        Task<string?> PostNetflixGraphQlAsync(string requestBody, CancellationToken ct = default);
        /// <summary>The browser's netflix.com cookies as a Cookie header (so Baird can call Netflix's API itself), or null.</summary>
        Task<string?> GetNetflixCookieHeaderAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        Task<string?> CaptureNetflixSearchRequestAsync(string searchTerm, CancellationToken ct = default);
        Task<string?> SelectNetflixProfileAsync(string? preferredName, CancellationToken ct = default);
        Task<SignInSubmitResult> SubmitCredentialsAsync(string user, string password, CancellationToken ct = default);
        Task<string?> GetLoginErrorTextAsync(CancellationToken ct = default);
        Task<bool> SubmitMfaPinAsync(string pin, CancellationToken ct = default);
        Task<VideoPlaybackStatus?> PollVideoPlaybackAsync(CancellationToken ct = default);
        Task<string?> ControlNetflixPlayerAsync(NetflixPlayerAction action, double seconds = 0, CancellationToken ct = default);
        Task<bool> InjectOverlayScriptAsync(string? script = null, CancellationToken ct = default);
        Task<bool> RevealWindowAsync(CancellationToken ct = default);
        /// <summary>The opposite of <see cref="RevealWindowAsync"/> for a browser that stays open: leave fullscreen and put Baird back on top.</summary>
        Task HideWindowAsync(CancellationToken ct = default);
        Task StopAsync();

        /// <summary>A message the overlay sent with <c>window.bairdSignal(text)</c>, e.g. <c>{"type":"exit"}</c>. Raised on a background thread.</summary>
        event Action<string>? PageSignalReceived;
    }

    public enum NetflixPlayerAction { Play, Pause, SeekBy }

    /// <summary>A request the page sent, as seen by CDP Network.requestWillBeSent (headers can include a bearer token: never log it).</summary>
    public sealed record CapturedRequest(string Url, string Method, string? PostData, IReadOnlyDictionary<string, string> Headers);

    /// <summary>
    /// Site-agnostic page primitives (evaluate, type, key press, request capture) for spikes that drive a site other than
    /// Netflix. Implemented by <see cref="CdpBrowserService"/> next to <see cref="ICdpBrowserService"/>.
    /// </summary>
    public interface ICdpPageDriver
    {
        /// <summary>Evaluates JS in the page (promises awaited) and returns its JSON-serialisable value, or null on error.</summary>
        Task<JsonElement?> EvaluateAsync(string script, CancellationToken ct = default);
        /// <summary>Focuses the element matching <paramref name="selector"/> and types <paramref name="text"/> with real input events. The text is never logged.</summary>
        Task<bool> TypeIntoAsync(string selector, string text, CancellationToken ct = default);
        Task PressEnterAsync(CancellationToken ct = default);
        /// <summary>Moves the page's pointer to (x, y): a streaming site's player UI wakes on pointer movement. Nothing is clicked.</summary>
        Task MoveMouseAsync(int x, int y, CancellationToken ct = default);
        /// <summary>Navigates to <paramref name="url"/> and returns the first request for which <paramref name="match"/> is true (or null after the timeout).</summary>
        Task<CapturedRequest?> NavigateAndCaptureAsync(string url, Func<CapturedRequest, bool> match, TimeSpan timeout, CancellationToken ct = default);
    }

    public class CdpBrowserService : ICdpBrowserService, ICdpPageDriver
    {
        private Process? _browserProcess;
        // CDP events (messages without an id) from the page socket go here while something is listening.
        private volatile Action<JsonElement>? _pageEventHandler;
        // Baird's own X window, so focus can go back to it when the browser closes.
        private string? _bairdWindowId;
        /// <summary>True after <see cref="RevealWindowAsync"/> raised the window above Baird, until it is hidden again or the browser stops.</summary>
        private bool _windowRevealed;

        /// <summary>Name of the page function (a CDP binding) the overlay calls to send a message to Baird.</summary>
        public const string PageSignalBinding = "bairdSignal";
        public event Action<string>? PageSignalReceived;
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
            _windowRevealed = false;

            _cdpPort = port;
            BrowserPath = browserPath;

            Directory.CreateDirectory(userDataDir);
            if (!ChromiumProfilePrefs.DisablePasswordManager(userDataDir)) Log("[CDP] Could not pre-seed the profile to disable the password manager.");

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var wv = WidevineStager.Stage(userDataDir);
                Log($"[CDP] Widevine: {wv.Detail}");
            }

            Log($"[CDP] Starting browser process: {browserPath}");
            Log($"[CDP] URL: {url}");
            Log($"[CDP] User data dir: {userDataDir}");
            Log(visible ? "[CDP] Mode: VISIBLE (sign-in), kiosk (frameless)" : "[CDP] Mode: kiosk (frameless), kept behind Baird until revealed");

            var psi = new ProcessStartInfo
            {
                FileName = browserPath,
                Arguments = $"--remote-debugging-port={port} " +
                            // --kiosk = fullscreen with no frame, tab strip or toolbar. Measured on the Pi (labwc): a normal or
                            // --app window gets a title bar, and --window-position=-4000,-4000 is ignored, so the old
                            // "hidden, offscreen" launch was visible and framed. Kiosk is frameless wherever it appears.
                            // Force the X11 backend: under the Pi's Wayland session Chromium otherwise runs as a native Wayland client,
                            // which xdotool cannot see, so the window could not be kept behind Baird or raised later.
                            (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ? "" : "--ozone-platform=x11 ") +
                            $"--kiosk " +
                            // No permission popups (Netflix asks for notifications): nobody can click them on a TV.
                            // The save-password bubble is disabled through profile prefs (ChromiumProfilePrefs) instead.
                            $"--deny-permission-prompts " +
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

            // Don't leak Baird's own runtime environment into the browser: the app's LD_LIBRARY_PATH points at its bundled
            // native libraries (SkiaSharp, HarfBuzz, ...) and .NET host variables are meaningless to Chromium.
            foreach (var key in psi.Environment.Keys.Cast<string>().ToList())
            {
                if (key == "LD_LIBRARY_PATH" || key == "LD_PRELOAD" ||
                    key.StartsWith("DOTNET_", StringComparison.Ordinal) || key.StartsWith("COMPlus_", StringComparison.Ordinal) ||
                    key.StartsWith("BAIRD_", StringComparison.Ordinal) || key.StartsWith("AVALONIA_", StringComparison.Ordinal))
                {
                    psi.Environment.Remove(key);
                }
            }

            // Chromium grabs keyboard focus when it opens; remember Baird's window so focus can go back to it.
            // Baird's own window (by our process), not "whatever is active": after a reboot something else can be active,
            // and handing focus back to that would leave the browser on top of Baird.
            var previousActive = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? FindLargestWindow($"search --pid {Environment.ProcessId}", 640) ?? RunShellOutput("xdotool", "getactivewindow")
                : null;
            _bairdWindowId = previousActive;

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
                _ = Task.Run(() => ReceiveLoopAsync(_pageWs, _pendingPageRequests, _recvCts.Token, OnPageEvent));
                Log("[CDP] Connected to Page DevTools WebSocket.");

                if (browserWsUrl != null)
                {
                    _browserWs = new ClientWebSocket();
                    await _browserWs.ConnectAsync(new Uri(browserWsUrl), ct);
                    _ = Task.Run(() => ReceiveLoopAsync(_browserWs, _pendingBrowserRequests, _recvCts.Token, null));
                    Log("[CDP] Connected to Browser DevTools WebSocket.");
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) await SettleWindowStackingAsync(visible, previousActive, ct);

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
                let membershipStatus = null;
                try { membershipStatus = window.netflix.reactContext.models.userInfo.data.membershipStatus || null; } catch (e) { }
                const hasSignInLink = !!document.querySelector('[data-uia=""header-signin-link""]');
                // Cookie-consent banner (OneTrust): accept so it never covers the TV.
                try { const ob = document.querySelector('#onetrust-accept-btn-handler'); if (ob) ob.click(); } catch (e) { }
                // Netflix error page ('Pardon the interruption ... Error Code E100').
                let playbackErrorCode = null, playbackErrorMessage = null;
                try {
                    const text = document.body ? document.body.innerText : '';
                    const m = text.match(/Error Code\s*([A-Z0-9._-]+)/);
                    if (m) {
                        playbackErrorCode = m[1];
                        const lines = text.split('\n').map(l => l.trim()).filter(Boolean);
                        playbackErrorMessage = lines.find(l => /please|unable|not available|went wrong/i.test(l)) || lines.slice(0, 3).join(' ');
                    }
                } catch (e) { }
                return {
                    playbackErrorCode: playbackErrorCode,
                    playbackErrorMessage: playbackErrorMessage,
                    membershipStatus: membershipStatus,
                    hasSignInLink: hasSignInLink,
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
                    CurrentTime: r.TryGetProperty("currentTime", out var c) ? c.GetDouble() : 0,
                    MembershipStatus: r.TryGetProperty("membershipStatus", out var ms) && ms.ValueKind == JsonValueKind.String ? ms.GetString() : null,
                    HasSignInLink: r.TryGetProperty("hasSignInLink", out var sl) && sl.ValueKind == JsonValueKind.True,
                    PlaybackErrorCode: r.TryGetProperty("playbackErrorCode", out var pc) && pc.ValueKind == JsonValueKind.String ? pc.GetString() : null,
                    PlaybackErrorMessage: r.TryGetProperty("playbackErrorMessage", out var pm) && pm.ValueKind == JsonValueKind.String ? pm.GetString() : null
                );
            }

            return null;
        }

        public async Task<bool> NavigateAsync(string url, CancellationToken ct = default)
        {
            if (_pageWs == null || _pageWs.State != WebSocketState.Open) return false;
            try
            {
                await SendPageCommandAsync("Page.navigate", new JsonObject { ["url"] = url }, ct);
                return true;
            }
            catch (Exception ex)
            {
                Log($"[CDP] Navigate failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads the Netflix session from an app page (e.g. /browse): signed out, member, and the "Who's watching?" picker.
        /// </summary>
        private const string NetflixSessionStateScript = @"(function() {
                let ms = null;
                try { ms = window.netflix.reactContext.models.userInfo.data.membershipStatus || null; } catch (e) { }
                const signedOut = ms === 'ANONYMOUS' || !!document.querySelector('input[name=""password""]') || !!document.querySelector('[data-uia=""header-signin-link""]');
                const profiles = [...document.querySelectorAll('[data-uia^=""profile-selector+tile-""]')].map(b => (b.innerText || '').trim()).filter(Boolean);
                return { url: location.href, signedOut: signedOut, member: ms === 'CURRENT_MEMBER', profiles: profiles };
            })()";

        public async Task<NetflixSessionState?> ReadNetflixSessionStateAsync(CancellationToken ct = default) =>
            ParseNetflixSessionState(await EvaluateInPageAsync(NetflixSessionStateScript, ct));

        public async Task<NetflixSessionState?> WatchNetflixSignInAsync(CancellationToken ct = default)
        {
            // The key listener is put back on every read, since the sign-in pages navigate.
            const string script = @"(function() {
                if (!window.__bairdNetflixBack) {
                    window.__bairdNetflixBack = true;
                    window.addEventListener('keydown', (e) => {
                        if (e.key === 'Escape' || e.key === 'BrowserBack' || e.key === 'GoBack') { e.preventDefault(); window.__bairdNetflixCancelled = true; }
                    }, true);
                }
                const state = " + NetflixSessionStateScript + @";
                state.cancelled = !!window.__bairdNetflixCancelled;
                return state;
            })()";
            var r = await EvaluateInPageAsync(script, ct);
            return ParseNetflixSessionState(r) is { } state
                ? state with { BackPressed = r!.Value.TryGetProperty("cancelled", out var c) && c.ValueKind == JsonValueKind.True }
                : null;
        }

        private static NetflixSessionState? ParseNetflixSessionState(JsonElement? r)
        {
            if (r is not { ValueKind: JsonValueKind.Object } o) return null;
            var profiles = new List<string>();
            if (o.TryGetProperty("profiles", out var pa) && pa.ValueKind == JsonValueKind.Array)
                foreach (var p in pa.EnumerateArray()) if (p.GetString() is { } name) profiles.Add(name);
            return new NetflixSessionState(
                o.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                o.TryGetProperty("signedOut", out var so) && so.ValueKind == JsonValueKind.True,
                o.TryGetProperty("member", out var m) && m.ValueKind == JsonValueKind.True,
                profiles);
        }

        /// <summary>
        /// POSTs a request body to Netflix's same-origin /graphql endpoint from inside the signed-in page (cookies included),
        /// as the web app does. Returns the raw reply text. The page must be on https://www.netflix.com.
        /// </summary>
        public async Task<string?> PostNetflixGraphQlAsync(string requestBody, CancellationToken ct = default)
        {
            var bodyLiteral = JsonSerializer.Serialize(requestBody, BairdJsonContext.Default.String);
            var script = @"(async function() {
                const r = await fetch('/graphql', { method: 'POST', credentials: 'include',
                    headers: { 'content-type': 'application/json' }, body: " + bodyLiteral + @" });
                return await r.text();
            })()";
            var r = await EvaluateInPageAsync(script, ct);
            return r is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        }

        public async Task<string?> GetNetflixCookieHeaderAsync(CancellationToken ct = default)
        {
            if (_pageWs == null || _pageWs.State != WebSocketState.Open) return null;
            try
            {
                var r = await SendPageCommandAsync("Network.getCookies", new JsonObject { ["urls"] = new JsonArray("https://www.netflix.com/") }, ct);
                if (!r.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array) return null;
                var pairs = cookies.EnumerateArray()
                    .Select(c => (Name: c.GetProperty("name").GetString(), Value: c.GetProperty("value").GetString()))
                    .Where(c => !string.IsNullOrEmpty(c.Name)).Select(c => $"{c.Name}={c.Value}").ToList();
                return pairs.Count > 0 ? string.Join("; ", pairs) : null;
            }
            catch (Exception ex) { Log($"[CDP] Couldn't read Netflix's cookies: {ex.Message}"); return null; }
        }

        /// <summary>
        /// Opens the real search page and captures the SearchPageQueryResults request Netflix's own web app sends, so a rotated
        /// persisted-query id or changed variables can be picked up without a Baird release. Returns the request body or null.
        /// </summary>
        public async Task<string?> CaptureNetflixSearchRequestAsync(string searchTerm, CancellationToken ct = default)
        {
            if (_pageWs == null || _pageWs.State != WebSocketState.Open) return null;
            var captured = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnEvent(JsonElement message)
            {
                if (message.TryGetProperty("method", out var m) && m.GetString() == "Network.requestWillBeSent" &&
                    message.TryGetProperty("params", out var p) && p.TryGetProperty("request", out var req) &&
                    req.TryGetProperty("postData", out var pd) && pd.GetString() is { } post && post.Contains("\"SearchPageQueryResults\""))
                {
                    captured.TrySetResult(post);
                }
            }

            _pageEventHandler = OnEvent;
            try
            {
                await SendPageCommandAsync("Network.enable", new JsonObject { ["maxPostDataSize"] = 1_000_000 }, ct);
                await SendPageCommandAsync("Page.navigate", new JsonObject { ["url"] = NetflixSearchParser.SearchUrl(searchTerm) }, ct);
                var done = await Task.WhenAny(captured.Task, Task.Delay(TimeSpan.FromSeconds(20), ct));
                return done == captured.Task ? captured.Task.Result : null;
            }
            catch (Exception ex)
            {
                Log($"[CDP] Capturing Netflix's search request failed: {ex.Message}");
                return null;
            }
            finally
            {
                _pageEventHandler = null;
                try { await SendPageCommandAsync("Network.disable", new JsonObject(), CancellationToken.None); } catch { }
            }
        }

        /// <summary>Clicks a profile on the "Who's watching?" picker: the named one if present, else the first. Returns the name clicked.</summary>
        public async Task<string?> SelectNetflixProfileAsync(string? preferredName, CancellationToken ct = default)
        {
            var wanted = JsonSerializer.Serialize(preferredName ?? "", BairdJsonContext.Default.String);
            var script = @"(function() {
                const wanted = " + wanted + @".trim().toLowerCase();
                const tiles = [...document.querySelectorAll('[data-uia^=""profile-selector+tile-""]')];
                if (!tiles.length) return null;
                const tile = (wanted && tiles.find(b => (b.innerText || '').trim().toLowerCase() === wanted)) || tiles[0];
                tile.click();
                return (tile.innerText || '').trim();
            })()";
            var r = await EvaluateInPageAsync(script, ct);
            return r is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
        }

        /// <summary>
        /// Types the credentials into Netflix's login form (userLoginId / password) with real input events and submits it.
        /// The values go straight into CDP Input.insertText; they are never logged.
        /// </summary>
        public async Task<SignInSubmitResult> SubmitCredentialsAsync(string user, string password, CancellationToken ct = default)
        {
            if (_pageWs == null || _pageWs.State != WebSocketState.Open) return new(false, "browser page is not connected");

            // Wait for the form (the SPA renders it after load).
            bool formReady = false;
            for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
            {
                var ready = await EvaluateInPageAsync("(function(){return !!document.querySelector('input[name=\"userLoginId\"]') && !!document.querySelector('input[name=\"password\"]');})()", ct);
                if (ready is { ValueKind: JsonValueKind.True }) { formReady = true; break; }
                await Task.Delay(500, ct);
            }
            if (!formReady) return new(false, "login form (userLoginId/password) did not appear");

            foreach (var (field, value) in new[] { ("userLoginId", user), ("password", password) })
            {
                var focused = await EvaluateInPageAsync($"(function(){{const e=document.querySelector('input[name=\"{field}\"]');if(!e)return false;e.focus();e.select();return true;}})()", ct);
                if (focused is not { ValueKind: JsonValueKind.True }) return new(false, $"could not focus the {field} field");
                await SendPageCommandAsync("Input.insertText", new JsonObject { ["text"] = value }, ct);
            }

            var clicked = await EvaluateInPageAsync(
                "(function(){const b=document.querySelector('[data-uia=\"login-submit-button\"]')||document.querySelector('button[type=\"submit\"]');if(!b)return false;b.click();return true;})()", ct);
            if (clicked is not { ValueKind: JsonValueKind.True })
            {
                // No recognisable submit button: press Enter in the password field.
                await EvaluateInPageAsync("(function(){const e=document.querySelector('input[name=\"password\"]');if(e)e.focus();})()", ct);
                foreach (var type in new[] { "keyDown", "keyUp" })
                {
                    await SendPageCommandAsync("Input.dispatchKeyEvent", new JsonObject
                    {
                        ["type"] = type,
                        ["key"] = "Enter",
                        ["code"] = "Enter",
                        ["windowsVirtualKeyCode"] = 13,
                        ["text"] = type == "keyDown" ? "\r" : ""
                    }, ct);
                }
                return new(true, "submitted with the Enter key (no submit button found)");
            }
            return new(true, "submitted");
        }

        /// <summary>Visible error text on the login page (e.g. wrong password), if any.</summary>
        public async Task<string?> GetLoginErrorTextAsync(CancellationToken ct = default)
        {
            var r = await EvaluateInPageAsync(
                "(function(){const e=document.querySelector('[data-uia*=\"error\"],[role=\"alert\"]');return e?(e.innerText||'').trim().slice(0,300):null;})()", ct);
            return r is { ValueKind: JsonValueKind.String } v && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;
        }

        /// <summary>
        /// Netflix answered E100 ("not available to watch instantly") for every title on the Pi until its saved site data was
        /// cleared (measured on the Pi: clearing local storage, IndexedDB, service workers and caches, with cookies kept, made the
        /// very same session play at once; a Widevine capability probe made no difference). Cookies stay, so the login persists.
        /// </summary>
        public async Task<string> ClearSiteDataAsync(string origin, CancellationToken ct = default)
        {
            try
            {
                await SendPageCommandAsync("Storage.clearDataForOrigin", new JsonObject
                {
                    ["origin"] = origin,
                    ["storageTypes"] = "local_storage,indexeddb,service_workers,cache_storage"
                }, ct);
                return $"cleared local storage, IndexedDB, service workers and caches for {origin} (cookies kept)";
            }
            catch (Exception ex)
            {
                return "clearing site data failed: " + ex.Message;
            }
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

        /// <summary>
        /// Plays, pauses or seeks through Netflix's own player API. A direct video.currentTime change can make Netflix
        /// fail (M7375), so the video element is only a fallback. Returns which path was used ("api" or "video"), or null
        /// if the page has no player.
        /// </summary>
        public async Task<string?> ControlNetflixPlayerAsync(NetflixPlayerAction action, double seconds = 0, CancellationToken ct = default)
        {
            var script = NetflixPlayerScript(action, seconds);
            var result = await EvaluateInPageAsync(script, ct);
            var path = result is { ValueKind: JsonValueKind.String } r ? r.GetString() : null;
            Log($"[CDP] Netflix player {action} {(action == NetflixPlayerAction.SeekBy ? seconds + "s " : "")}-> {path ?? "no player"}");
            return path;
        }

        public static string NetflixPlayerScript(NetflixPlayerAction action, double seconds)
        {
            var arg = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $@"(function(action, seconds) {{
                const v = document.querySelector('video');
                let p = null;
                try {{
                    const vp = netflix.appContext.state.playerApp.getAPI().videoPlayer;
                    const ids = vp.getAllPlayerSessionIds();
                    const id = ids.find(i => i.startsWith('watch')) || ids[ids.length - 1];
                    p = id ? vp.getVideoPlayerBySessionId(id) : null;
                }} catch (e) {{ p = null; }}
                if (action === 'SeekBy') {{
                    if (p) {{
                        const target = p.getCurrentTime() + seconds * 1000;
                        p.seek(Math.max(0, Math.min(target, p.getDuration() - 1000)));
                        return 'api';
                    }}
                    if (v) {{ v.currentTime = Math.max(0, v.currentTime + seconds); return 'video'; }}
                    return null;
                }}
                if (action === 'Play') {{
                    if (p) {{ p.play(); return 'api'; }}
                    if (v) {{ v.play(); return 'video'; }}
                    return null;
                }}
                if (action === 'Pause') {{
                    if (p) {{ p.pause(); return 'api'; }}
                    if (v) {{ v.pause(); return 'video'; }}
                    return null;
                }}
                return null;
            }})('{action}', {arg})";
        }

        private void OnPageEvent(JsonElement message)
        {
            if (TryReadPageSignal(message, out var payload))
            {
                Log($"[CDP] Page signal: {payload}");
                PageSignalReceived?.Invoke(payload);
            }
            _pageEventHandler?.Invoke(message);
        }

        /// <summary>True if <paramref name="message"/> is a call of the <see cref="PageSignalBinding"/> page function.</summary>
        public static bool TryReadPageSignal(JsonElement message, out string payload)
        {
            payload = "";
            if (!message.TryGetProperty("method", out var m) || m.GetString() != "Runtime.bindingCalled") return false;
            if (!message.TryGetProperty("params", out var p) ||
                !p.TryGetProperty("name", out var name) || name.GetString() != PageSignalBinding) return false;
            payload = p.TryGetProperty("payload", out var pl) ? pl.GetString() ?? "" : "";
            return true;
        }

        public async Task<bool> InjectOverlayScriptAsync(string? script = null, CancellationToken ct = default)
        {
            if (_pageWs != null && _pageWs.State == WebSocketState.Open)
            {
                // Gives the page window.bairdSignal(text). The binding stays across navigations and reloads.
                try { await SendPageCommandAsync("Runtime.addBinding", new JsonObject { ["name"] = PageSignalBinding }, ct); }
                catch (Exception ex) { Log($"[CDP] Runtime.addBinding failed; the overlay cannot signal Baird: {ex.Message}"); }
            }

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

        /// <summary>The page style for Baird-drawn HUD mode (Resources/netflix-page-style.js), embedded in the app.</summary>
        /// <summary>Hides Disney+'s own player controls (see disney-page-style.js).</summary>
        public static string LoadDisneyPageStyleScript()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Baird.Resources.disney-page-style.js");
            if (stream == null) return "console.log('[Baird] Disney+ page style not found.'); false";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public static string LoadChannel4PageStyleScript()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Baird.Resources.channel4-page-style.js");
            if (stream == null) return "console.log('[Baird] Channel 4 page style not found.'); false";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public static string LoadItvxPageStyleScript()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Baird.Resources.itvx-page-style.js");
            if (stream == null) return "console.log('[Baird] ITVX page style not found.'); false";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public static string LoadNetflixPageStyleScript()
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Baird.Resources.netflix-page-style.js");
            if (stream == null) return "console.log('[Baird] Netflix page style not found.'); false";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public async Task<bool> RevealWindowAsync(CancellationToken ct = default)
        {
            Log("[CDP] Revealing browser window: raising it above Baird (fullscreen)...");
            _windowRevealed = true;

            // 1. Attempt CDP Browser.setWindowBounds
            if (_browserWs != null && _browserWs.State == WebSocketState.Open)
            {
                try
                {
                    var windowForTarget = await SendBrowserCommandAsync("Browser.getWindowForTarget", new JsonObject(), ct);
                    if (windowForTarget.TryGetProperty("windowId", out var widProp))
                    {
                        var windowId = widProp.GetInt32();
                        await SendBrowserCommandAsync("Browser.setWindowBounds", new JsonObject
                        {
                            ["windowId"] = windowId,
                            ["bounds"] = new JsonObject { ["windowState"] = "fullscreen" } // CDP rejects a state combined with left/top/size
                        }, ct);
                        Log($"[CDP] Set window state via CDP (windowId: {windowId}) to fullscreen.");
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
            var browserWindow = FindBrowserWindowId(minWidth: 640);
            if (browserWindow != null)
            {
                RunShellCommand("xdotool", $"windowactivate {browserWindow}");
                Log("[CDP] Window stacking: browser raised above Baird.");
            }
            else
            {
                Log("[CDP] Window stacking: could not find the browser's window to raise it.");
            }

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

        public async Task HideWindowAsync(CancellationToken ct = default)
        {
            // Baird-HUD mode never reveals the window: it is still the lowered kiosk window and must stay fullscreen.
            if (!_windowRevealed) return;
            _windowRevealed = false;
            Log("[CDP] Hiding the browser window: leaving fullscreen and putting Baird back on top...");
            try
            {
                await EvaluateInPageAsync("if (document.fullscreenElement && document.exitFullscreen) { document.exitFullscreen().catch(() => {}); }", ct);
            }
            catch { }
            RunShellCommand("wmctrl", "-x -r baird-netflix -b remove,fullscreen");
            if (!string.IsNullOrEmpty(_bairdWindowId))
            {
                // Raise as well as focus: under labwc/XWayland focus alone can leave the browser stacked above Baird.
                RunShellCommand("xdotool", $"windowraise {_bairdWindowId} windowactivate {_bairdWindowId}");
                Log("[CDP] Window stacking: Baird raised above the browser.");
            }
            else
            {
                Log("[CDP] Window stacking: Baird's window id is unknown, so the browser could not be put behind it.");
            }
        }

        /// <summary>
        /// Chromium's window is lowered below Baird by a labwc windowRule (class baird-netflix, see configure_pi.sh), but it
        /// still takes keyboard focus when it opens. Hidden mode: hand focus back to Baird so the remote keeps driving Baird's UI.
        /// Visible (sign-in) mode: raise Chromium so the user can type into it.
        /// </summary>
        private async Task SettleWindowStackingAsync(bool visible, string? previousActive, CancellationToken ct)
        {
            // Chromium also creates a tiny (10x10) helper X window with the same class, so pick the biggest one, and wait for
            // it: handing focus back before the real window has mapped just lets the real window open on top afterwards.
            string? chromiumWindow = null;
            for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                chromiumWindow = FindBrowserWindowId(minWidth: 640);
                if (chromiumWindow != null) break;
                await Task.Delay(100, ct);
            }

            if (chromiumWindow == null)
            {
                Log("[CDP] Window stacking: could not find the browser's window via xdotool; leaving it as is. " +
                    $"(XDG_SESSION_TYPE={Environment.GetEnvironmentVariable("XDG_SESSION_TYPE")}, DISPLAY={Environment.GetEnvironmentVariable("DISPLAY")}, " +
                    $"WAYLAND_DISPLAY={Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")})");
                return;
            }

            await Task.Delay(100, ct); // brief: any longer and the window is visible on top of Baird
            if (visible)
            {
                RunShellCommand("xdotool", $"windowactivate {chromiumWindow}");
                Log("[CDP] Window stacking: browser raised for sign-in.");
            }
            else if (!string.IsNullOrEmpty(previousActive))
            {
                RunShellCommand("xdotool", $"windowraise {previousActive} windowactivate {previousActive}");
                Log("[CDP] Window stacking: browser stays behind Baird; focus returned to Baird.");
            }
            else
            {
                Log("[CDP] Window stacking: browser should be behind Baird, but Baird's window id is unknown so focus was not returned.");
            }
        }

        /// <summary>The browser's main X window (largest window of class baird-netflix), or null if none is big enough yet.</summary>
        private static string? FindBrowserWindowId(int minWidth) => FindLargestWindow("search --class baird-netflix", minWidth);

        /// <summary>The biggest window an xdotool search finds (apps also make tiny helper windows).</summary>
        private static string? FindLargestWindow(string search, int minWidth)
        {
            var ids = RunShellOutput("xdotool", search);
            if (ids == null) return null;

            string? best = null;
            long bestArea = 0;
            foreach (var id in ids.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var geo = RunShellOutput("xdotool", $"getwindowgeometry --shell {id}");
                if (geo == null) continue;
                long w = 0, h = 0;
                foreach (var line in geo.Split('\n'))
                {
                    if (line.StartsWith("WIDTH=") && long.TryParse(line.AsSpan(6), out var pw)) w = pw;
                    if (line.StartsWith("HEIGHT=") && long.TryParse(line.AsSpan(7), out var ph)) h = ph;
                }
                if (w >= minWidth && w * h > bestArea) { best = id; bestArea = w * h; }
            }
            return best;
        }

        private static string? RunShellOutput(string fileName, string args)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                });
                if (proc == null) return null;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(2000);
                return string.IsNullOrEmpty(output) ? null : output;
            }
            catch { return null; }
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

        public Task<JsonElement?> EvaluateAsync(string script, CancellationToken ct = default) => EvaluateInPageAsync(script, ct);

        public async Task<bool> TypeIntoAsync(string selector, string text, CancellationToken ct = default)
        {
            var sel = JsonSerializer.Serialize(selector, BairdJsonContext.Default.String);
            var focused = await EvaluateInPageAsync($"(function(){{const e=document.querySelector({sel});if(!e)return false;e.focus();if(e.select)e.select();return true;}})()", ct);
            if (focused is not { ValueKind: JsonValueKind.True }) return false;
            await SendPageCommandAsync("Input.insertText", new JsonObject { ["text"] = text }, ct);
            return true;
        }

        public async Task MoveMouseAsync(int x, int y, CancellationToken ct = default)
        {
            await SendPageCommandAsync("Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = x, ["y"] = y }, ct);
        }

        public async Task PressEnterAsync(CancellationToken ct = default)
        {
            foreach (var type in new[] { "keyDown", "keyUp" })
            {
                await SendPageCommandAsync("Input.dispatchKeyEvent", new JsonObject
                {
                    ["type"] = type,
                    ["key"] = "Enter",
                    ["code"] = "Enter",
                    ["windowsVirtualKeyCode"] = 13,
                    ["text"] = type == "keyDown" ? "\r" : ""
                }, ct);
            }
        }

        public async Task<CapturedRequest?> NavigateAndCaptureAsync(string url, Func<CapturedRequest, bool> match, TimeSpan timeout, CancellationToken ct = default)
        {
            if (_pageWs == null || _pageWs.State != WebSocketState.Open) return null;
            var captured = new TaskCompletionSource<CapturedRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnEvent(JsonElement message)
            {
                if (!message.TryGetProperty("method", out var m) || m.GetString() != "Network.requestWillBeSent" ||
                    !message.TryGetProperty("params", out var p) || !p.TryGetProperty("request", out var req)) return;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (req.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                    foreach (var kv in h.EnumerateObject()) headers[kv.Name] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : kv.Value.GetRawText();
                var cr = new CapturedRequest(
                    req.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "",
                    req.TryGetProperty("method", out var me) ? me.GetString() ?? "" : "",
                    req.TryGetProperty("postData", out var pd) ? pd.GetString() : null,
                    headers);
                try { if (match(cr)) captured.TrySetResult(cr); } catch { }
            }

            _pageEventHandler = OnEvent;
            try
            {
                await SendPageCommandAsync("Network.enable", new JsonObject { ["maxPostDataSize"] = 1_000_000 }, ct);
                await SendPageCommandAsync("Page.navigate", new JsonObject { ["url"] = url }, ct);
                var done = await Task.WhenAny(captured.Task, Task.Delay(timeout, ct));
                return done == captured.Task ? captured.Task.Result : null;
            }
            catch (Exception ex)
            {
                Log($"[CDP] Request capture failed: {ex.Message}");
                return null;
            }
            finally
            {
                _pageEventHandler = null;
                try { await SendPageCommandAsync("Network.disable", new JsonObject(), CancellationToken.None); } catch { }
            }
        }

        private async Task<JsonElement?> EvaluateInPageAsync(string script, CancellationToken ct = default)
        {
            if (_pageWs == null || _pageWs.State != WebSocketState.Open) return null;

            var result = await SendPageCommandAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = script,
                ["returnByValue"] = true,
                ["awaitPromise"] = true
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

        private async Task<JsonElement> SendPageCommandAsync(string method, JsonObject @params, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _pageReqId);
            return await SendCommandAsync(_pageWs!, _pendingPageRequests, id, method, @params, ct);
        }

        private async Task<JsonElement> SendBrowserCommandAsync(string method, JsonObject @params, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _browserReqId);
            return await SendCommandAsync(_browserWs!, _pendingBrowserRequests, id, method, @params, ct);
        }

        /// <summary>
        /// How long one CDP command may take to answer. Measured on the Pi: a Page.navigate to Disney+'s page never answered for
        /// 3 minutes while it held the shared gate, so Netflix's search waited behind it too. A hung command now fails with a
        /// TimeoutException that says which command, and callers (which already catch exceptions) carry on or report it.
        /// </summary>
        public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(45);

        private static async Task<JsonElement> SendCommandAsync(
            ClientWebSocket ws,
            ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending,
            int id,
            string method,
            JsonObject @params,
            CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(CommandTimeout);
            using var reg = timeout.Token.Register(() =>
            {
                if (pending.TryRemove(id, out var p))
                {
                    p.TrySetCanceled(timeout.Token);
                }
            });

            // JsonObject, not anonymous types: the shipped build is trimmed with reflection-based JSON disabled.
            var payload = new JsonObject
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params
            }.ToJsonString();

            var bytes = Encoding.UTF8.GetBytes(payload);
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, timeout.Token);
                return await tcs.Task;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                pending.TryRemove(id, out _);
                throw new TimeoutException($"The browser did not answer CDP {method} within {CommandTimeout.TotalSeconds:F0} s.");
            }
        }

        private static async Task ReceiveLoopAsync(
            ClientWebSocket ws,
            ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending,
            CancellationToken ct,
            Action<JsonElement>? onEvent)
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

                    if (!root.TryGetProperty("id", out _) && onEvent != null)
                    {
                        try { onEvent(root); } catch { /* a listener must never kill the socket */ }
                        continue;
                    }

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
                    await SendBrowserCommandAsync("Browser.close", new JsonObject(), closeCts.Token);
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

                // A revealed browser had focus; give it back to Baird so the remote drives Baird's UI again.
                if (!string.IsNullOrEmpty(_bairdWindowId))
                {
                    RunShellCommand("xdotool", $"windowactivate {_bairdWindowId}");
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
