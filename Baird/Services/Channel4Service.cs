using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>
    /// Channel 4 on Baird's shared browser (<see cref="BrowserHub"/>), for playing: the twin of <see cref="DisneyService"/>, but
    /// smaller, because search and episode lists do not need the browser (<see cref="Channel4Catalogue"/>). It takes the tab and
    /// returns it to <see cref="IdleUrl"/>, a page on Channel 4's origin with no video.
    /// <para>It never signs in itself: Channel 4's sign-in refuses automated browsers (reCAPTCHA Enterprise answers
    /// <c>E_RECAPTCHA_THRESHOLD</c>; on the Pi an Akamai block page). The viewer signs in by hand, once, in Baird's browser profile:
    /// on the TV (<see cref="SignInByHandAsync"/> shows the sign-in page and waits), or from a computer with <c>channel4_signin.sh</c>.
    /// Baird then plays with that session.</para>
    /// </summary>
    public sealed class Channel4Service
    {
        public const string Owner = "Channel 4";
        public const string IdleUrl = "https://www.channel4.com/robots.txt";
        /// <summary>Channel 4's sign-in page (channel4_signin.sh opens it for the viewer).</summary>
        public const string SignInUrl = "https://www.channel4.com/sign-in-or-register";

        private readonly BrowserHub _hub;

        /// <summary>What the viewer is told when Channel 4's page asks for a sign-in.</summary>
        public const string NotSignedInMessage =
            "Channel 4 is not signed in on this Baird.";

        public Channel4Service(BrowserHub hub, Channel4Catalogue catalogue)
        {
            if (hub.Browser is not ICdpPageDriver) throw new ArgumentException("the browser must also implement ICdpPageDriver");
            _hub = hub;
            Catalogue = catalogue;
        }

        public BrowserHub Hub => _hub;
        public ICdpBrowserService Browser => _hub.Browser;
        public ICdpPageDriver PageDriver => (ICdpPageDriver)_hub.Browser;
        public Channel4Catalogue Catalogue { get; }

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
        /// <summary>How long the sign-in page stays on screen for the viewer before Baird gives up and comes back.</summary>
        public TimeSpan HandSignInTimeout { get; set; } = TimeSpan.FromMinutes(10);

        public event Action<string>? Log;

        /// <summary>True while Channel 4 plays in the shared browser; the other services then stay away from the tab.</summary>
        public bool IsPlaybackActive
        {
            get => _hub.ActivePlayback == Owner;
            set { if (value) _hub.ActivePlayback = Owner; else if (_hub.ActivePlayback == Owner) _hub.ActivePlayback = null; }
        }

        /// <summary>
        /// Opens (or attaches to) the browser and takes the tab for Channel 4, marking Channel 4 as playing before the shared gate is
        /// released (so another service's warm-up cannot take the tab in between). Returns null when ready, else the reason.
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

        /// <summary>Ends Channel 4's use of the tab: Baird back on top, the tab on the idle page, then the playing flag cleared (last).</summary>
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
        /// Shows Channel 4's sign-in page on the screen, above Baird, for the VIEWER to sign in with their keyboard (Baird types
        /// nothing), and waits until a page says they are signed in, they press Back (Escape), or <see cref="HandSignInTimeout"/>
        /// passes. Channel 4 sends the browser to <paramref name="returnUrl"/> (a programme page, which says whether the viewer is signed
        /// in) when it is done. Then Baird is put back on top and the tab returns to its idle page.
        /// </summary>
        public async Task<Channel4SignInOutcome> SignInByHandAsync(string returnUrl, CancellationToken ct)
        {
            var failure = await BeginPlaybackAsync(ct); // the tab is Channel 4's until the viewer is done
            if (failure != null) return Channel4SignInOutcome.Failed(failure);
            try
            {
                if (!await Browser.NavigateAsync($"{SignInUrl}?redirectUrl={Uri.EscapeDataString(returnUrl)}", ct))
                    return Channel4SignInOutcome.Failed("The browser did not open Channel 4's sign-in page.");
                await Browser.RevealWindowAsync(ct);
                Log?.Invoke("Channel 4's sign-in page is on the screen for the viewer.");

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < HandSignInTimeout)
                {
                    await Task.Delay(PollInterval, ct);
                    if (await Channel4PlayerControl.WatchSignInAsync(PageDriver, ct) is not { } seen) continue;
                    // The page's own close (X) sends the browser back to the programme page, still signed out: the viewer gave up too.
                    var closed = seen.State.Authenticated == false && !seen.State.Url.Contains("/sign-in", StringComparison.Ordinal);
                    if (seen.Cancelled || closed)
                    {
                        Log?.Invoke(closed ? "The viewer closed Channel 4's sign-in page." : "The viewer pressed Back on Channel 4's sign-in page.");
                        return Channel4SignInOutcome.Cancelled;
                    }
                    if (seen.State.Authenticated == true)
                    {
                        Log?.Invoke($"The viewer signed in to Channel 4 ({sw.Elapsed.TotalSeconds:F0} s).");
                        return Channel4SignInOutcome.SignedIn;
                    }
                }
                Log?.Invoke($"Nobody signed in to Channel 4 within {HandSignInTimeout.TotalMinutes:F0} minutes.");
                return Channel4SignInOutcome.TimedOut;
            }
            catch (OperationCanceledException) { return Channel4SignInOutcome.Cancelled; }
            catch (Exception ex) { return Channel4SignInOutcome.Failed($"Channel 4's sign-in page failed: {ex.Message}"); }
            finally { await EndPlaybackAsync(); } // Baird back on top (the window was raised), the tab on the idle page
        }
    }

    /// <summary>How a sign-in by hand ended; <see cref="Message"/> is for the viewer (null when signed in).</summary>
    public sealed record Channel4SignInOutcome(bool IsSignedIn, string? Message)
    {
        public static readonly Channel4SignInOutcome SignedIn = new(true, null);
        public static readonly Channel4SignInOutcome Cancelled = new(false, "Channel 4 sign-in cancelled.");
        public static readonly Channel4SignInOutcome TimedOut = new(false, "Channel 4 sign-in took too long, so Baird came back.");
        public static Channel4SignInOutcome Failed(string reason) => new(false, reason);
    }
}
