using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>
    /// ITVX on Baird's shared browser (<see cref="BrowserHub"/>): the twin of <see cref="Channel4Service"/>. Search does not need the
    /// browser (<see cref="ItvxCatalogue"/>), but programme pages and playing do, because www.itv.com answers only real browsers. It
    /// takes the tab and returns it to <see cref="IdleUrl"/>, a page on ITVX's origin with no video, from which programme pages can be
    /// fetched.
    /// <para>It signs in with <see cref="ItvxCredentials"/> when a page asks for it (measured on the Pi, 2026-10-26: no captcha; ITVX
    /// emails a code by default, and "Use password instead" leads to the password). One attempt per run after a refusal, so a wrong
    /// password cannot lock the account.</para>
    /// </summary>
    public sealed class ItvxService
    {
        public const string Owner = "ITVX";
        public const string IdleUrl = "https://www.itv.com/robots.txt";
        public const string SignInUrl = "https://www.itv.com/watch/user/signin";

        /// <summary>What the viewer is told when ITVX asks for a sign-in and Baird has no credentials.</summary>
        public const string NoCredentialsMessage = "ITVX needs a sign-in: add ITVX_USER and ITVX_PASS to Baird's config.ini.";

        private readonly BrowserHub _hub;
        private readonly ConcurrentDictionary<string, (DateTime At, ItvxProgramme Programme)> _programmes = new();
        private string? _signInRefused;

        public ItvxService(BrowserHub hub, ItvxCatalogue catalogue, ItvxCredentials? credentials)
        {
            if (hub.Browser is not ICdpPageDriver) throw new ArgumentException("the browser must also implement ICdpPageDriver");
            _hub = hub;
            Catalogue = catalogue;
            Credentials = credentials;
        }

        public BrowserHub Hub => _hub;
        public ICdpBrowserService Browser => _hub.Browser;
        public ICdpPageDriver PageDriver => (ICdpPageDriver)_hub.Browser;
        public ItvxCatalogue Catalogue { get; }
        public ItvxCredentials? Credentials { get; }

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
        public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromSeconds(45);
        /// <summary>How long a programme page is reused before it is read again.</summary>
        public TimeSpan ProgrammeLifetime { get; set; } = TimeSpan.FromMinutes(10);

        public event Action<string>? Log;

        /// <summary>True while ITVX plays in the shared browser; the other services then stay away from the tab.</summary>
        public bool IsPlaybackActive
        {
            get => _hub.ActivePlayback == Owner;
            set { if (value) _hub.ActivePlayback = Owner; else if (_hub.ActivePlayback == Owner) _hub.ActivePlayback = null; }
        }

        /// <summary>
        /// Opens (or attaches to) the browser and takes the tab for ITVX, marking ITVX as playing before the shared gate is released (so
        /// another service's warm-up cannot take the tab in between). Returns null when ready, else the reason.
        /// </summary>
        public async Task<string?> BeginPlaybackAsync(CancellationToken ct)
        {
            await _hub.Gate.WaitAsync(ct);
            try
            {
                if (_hub.BlockedFor(Owner) is { } blocked) return blocked;
                if (!Browser.IsRunning)
                {
                    Log?.Invoke("Opening the browser (hidden behind Baird)...");
                    var launchFailure = await _hub.EnsureRunningAsync(IdleUrl, ct);
                    if (launchFailure != null) return launchFailure;
                }
                _hub.PageOwner = Owner;
                IsPlaybackActive = true;
                return null;
            }
            finally { _hub.Gate.Release(); }
        }

        /// <summary>Ends ITVX's use of the tab: Baird back on top, the tab on the idle page, then the playing flag cleared (last).</summary>
        public async Task EndPlaybackAsync()
        {
            try
            {
                if (Browser.IsRunning)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await Browser.HideWindowAsync(cts.Token);
                    if (!await Browser.NavigateAsync(IdleUrl, cts.Token)) Log?.Invoke("Could not return the browser to the idle page.");
                }
            }
            catch (Exception ex) { Log?.Invoke($"Could not return the browser to the idle page: {ex.Message}"); }
            finally { IsPlaybackActive = false; }
        }

        /// <summary>
        /// A programme's series and episodes, read from its page in the shared browser (reused for <see cref="ProgrammeLifetime"/>). Works
        /// while ITVX plays (the fetch runs in the playing page and does not touch the video); throws while another service plays, and
        /// on any failure, with the reason.
        /// </summary>
        public async Task<ItvxProgramme> GetProgrammeAsync(string slug, string ccid, CancellationToken ct)
        {
            var key = $"{slug}/{ccid}";
            if (_programmes.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < ProgrammeLifetime) return cached.Programme;

            await _hub.Gate.WaitAsync(ct);
            JsonElement? result;
            try
            {
                if (_hub.BlockedFor(Owner) is { } blocked) throw new InvalidOperationException(blocked);
                if (!Browser.IsRunning)
                {
                    Log?.Invoke("Opening the browser (hidden behind Baird)...");
                    if (await _hub.EnsureRunningAsync(IdleUrl, ct) is { } launchFailure) throw new InvalidOperationException(launchFailure);
                    _hub.PageOwner = Owner;
                }
                // The fetch must come from a page on www.itv.com. While ITVX plays the tab is already there.
                if (_hub.PageOwner != Owner && !IsPlaybackActive)
                {
                    if (!await Browser.NavigateAsync(IdleUrl, ct)) throw new InvalidOperationException("The browser did not open ITVX.");
                    _hub.PageOwner = Owner;
                }
                var sw = Stopwatch.StartNew();
                result = await PageDriver.EvaluateAsync(ItvxPlayerControl.ProgrammeFetchScript($"/watch/{slug}/{ccid}"), ct);
                Log?.Invoke($"Read ITVX's page for {slug} ({sw.ElapsedMilliseconds} ms).");
            }
            finally { _hub.Gate.Release(); }

            if (result is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("error", out var error))
                throw new InvalidOperationException($"ITVX's page for {slug}: {error}");
            if (result is not { ValueKind: JsonValueKind.String } json)
                throw new InvalidOperationException($"ITVX's page for {slug} could not be read in the browser.");
            var programme = ItvxCatalogue.ParseProgramme(json.GetString(), out var failure)
                ?? throw new InvalidOperationException($"ITVX's page for {slug}: {failure}");
            _programmes[key] = (DateTime.UtcNow, programme);
            return programme;
        }

        /// <summary>
        /// Signs in on ITVX's sign-in pages with <see cref="Credentials"/>, in the tab ITVX holds (call it between
        /// <see cref="BeginPlaybackAsync"/> and <see cref="EndPlaybackAsync"/>). Returns null when signed in, else the reason. The password is
        /// typed into the page and never logged.
        /// </summary>
        public async Task<string?> SignInAsync(CancellationToken ct)
        {
            if (Credentials is not { } credentials) return NoCredentialsMessage;
            if (_signInRefused != null) return _signInRefused; // one refused attempt per run: no retries against the account

            Log?.Invoke("Signing in to ITVX...");
            if (!await Browser.NavigateAsync(SignInUrl, ct)) return "The browser did not open ITVX's sign-in page.";

            var sw = Stopwatch.StartNew();
            bool emailSent = false, passwordSent = false, usePassword = false;
            ItvxPageState? last = null;
            while (sw.Elapsed < SignInTimeout)
            {
                await Task.Delay(PollInterval, ct);
                var state = await ItvxPlayerControl.ReadStateAsync(PageDriver, ct);
                if (state == null) continue;
                last = state;

                if (!state.SignInPage && state.SignedIn)
                {
                    Log?.Invoke($"Signed in to ITVX ({sw.Elapsed.TotalSeconds:F0} s).");
                    return null;
                }
                if (state.PasswordField && !passwordSent)
                {
                    passwordSent = true;
                    if (!await PageDriver.TypeIntoAsync("input[type=password]", credentials.Password, ct)) return "Could not type into ITVX's password field.";
                    await PageDriver.EvaluateAsync(ItvxPlayerControl.PressButtonScript("sign in"), ct);
                    Log?.Invoke("Password entered.");
                    continue;
                }
                // ITVX emails a sign-in code by default; the link under it asks for the password instead.
                if (state.Url.Contains("/email-pair", StringComparison.Ordinal) && !usePassword)
                {
                    usePassword = true;
                    await PageDriver.EvaluateAsync(ItvxPlayerControl.UsePasswordScript, ct);
                    Log?.Invoke("Asked for the password instead of an emailed code.");
                    continue;
                }
                if (state.EmailField && !emailSent && !state.PasswordField)
                {
                    emailSent = true;
                    if (!await PageDriver.TypeIntoAsync("input[type=email]", credentials.User, ct)) return "Could not type into ITVX's email field.";
                    await PageDriver.EvaluateAsync(ItvxPlayerControl.PressButtonScript("continue"), ct);
                    Log?.Invoke("Email entered.");
                }
            }
            var reason = $"ITVX sign-in did not finish within {SignInTimeout.TotalSeconds:F0} s. Last page: {last?.Url ?? "unreadable"} \"{last?.Text}\"";
            if (passwordSent) _signInRefused = reason; // the password went in and did not work: do not try again this run
            Log?.Invoke(reason);
            return reason;
        }
    }
}
