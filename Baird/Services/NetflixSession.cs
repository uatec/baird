using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>Result of one Netflix search: the reply, or why there is none.</summary>
    public sealed record NetflixSearchOutcome(NetflixSearchReply? Reply, string? Failure, long ElapsedMs)
    {
        public bool Ok => Failure == null && Reply is { Ok: true };
    }

    /// <summary>
    /// The one signed-in Chromium that Baird uses for Netflix: search and playback share it, because they share the browser
    /// profile and Chromium locks a profile to one process. <see cref="Start"/> opens it hidden behind Baird soon after Baird
    /// starts, signs in and picks the profile, so the first search or play does not wait for that. It then stays open: a
    /// playback navigates it to the watch URL and back to <see cref="IdleUrl"/>, and a watchdog opens it again if it stops.
    /// <para>When signing in with the configured credentials fails, or Netflix answers it with a verification step, Baird does
    /// not try again in the background (each try can send the viewer another code): the next play asks the viewer, either for
    /// the code (<see cref="SubmitVerificationCodeAsync"/>) or to sign in by hand on the TV (<see cref="SignInByHandAsync"/>).</para>
    /// </summary>
    public sealed class NetflixSession : IAsyncDisposable
    {
        public const int CdpPort = 9222;
        public const string BrowseUrl = "https://www.netflix.com/browse";
        /// <summary>
        /// Where the browser waits between uses: on Netflix's origin (the search API is a same-origin fetch with the cookies)
        /// but with no video. Covered Chromium keeps rendering, so an idle /browse page would decode its trailer all the time.
        /// </summary>
        public const string IdleUrl = "https://www.netflix.com/robots.txt";

        private const string Owner = "Netflix";
        public const string LoginUrl = "https://www.netflix.com/login";
        /// <summary>What the viewer is told when Netflix stopped a sign-in to ask for the code it sent them.</summary>
        public const string VerificationCodeMessage = "Netflix asked for the verification code it sent you.";
        /// <summary>What the viewer is told when Netflix still shows its verification step after a code was sent.</summary>
        public const string CodeRefusedMessage = "Netflix did not accept that code.";
        private readonly BrowserHub _hub;
        private readonly string? _profileName;
        private int _memberConfirmedPid;
        /// <summary>Why the last sign-in with the configured credentials failed; set, the background does not sign in again.</summary>
        private volatile string? _signInFailure;
        private readonly CancellationTokenSource _lifetime = new();

        public ICdpBrowserService Browser => _hub.Browser;
        public NetflixCredentials? Credentials { get; }

        /// <summary>The shared browser (also used by Disney+).</summary>
        public BrowserHub Hub => _hub;

        /// <summary>True while a Netflix playback flow owns the browser; search then answers with a failure instead of disturbing it.</summary>
        public bool IsPlaybackActive
        {
            get => _hub.ActivePlayback == Owner;
            set { if (value) _hub.ActivePlayback = Owner; else if (_hub.ActivePlayback == Owner) _hub.ActivePlayback = null; }
        }

        public int Port { get => _hub.Port; set => _hub.Port = value; }
        public string ProfileDir { get => _hub.ProfileDir; set => _hub.ProfileDir = value; }
        /// <summary>Where a refreshed copy of Netflix's search request is cached (next to the browser profile).</summary>
        public string QueryCachePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "baird", "netflix-search-query.json");
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
        /// <summary>Pause after submitting credentials / picking a profile before reading the page again.</summary>
        public TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(4);
        /// <summary>How often the watchdog checks that the browser is still open.</summary>
        public TimeSpan WatchdogInterval { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>How long Netflix's sign-in page stays on screen for the viewer before Baird gives up and comes back.</summary>
        public TimeSpan HandSignInTimeout { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>Netflix is not signed in and Baird could not sign in itself: the viewer has to (by hand, or with a code).</summary>
        public bool NeedsSignIn => _signInFailure != null;

        /// <summary>Baird has seen a signed-in member on a chosen profile in the browser that's running now.</summary>
        public bool IsSignedIn => _signInFailure == null && Browser.IsRunning && _memberConfirmedPid == Browser.ProcessId;

        /// <summary>The browser is on Netflix's verification step after a sign-in, waiting for the code Netflix sent the viewer.</summary>
        public bool NeedsVerificationCode { get; private set; }

        public event Action<string>? Log;

        /// <param name="hub">The shared browser. Omit it for a Netflix-only browser (spikes, tests).</param>
        public NetflixSession(ICdpBrowserService browser, NetflixCredentials? credentials, string? profileName = null, BrowserHub? hub = null)
        {
            _hub = hub ?? new BrowserHub(browser) { Port = CdpPort };
            Credentials = credentials;
            _profileName = profileName;
        }

        private void AddLog(string message) => Log?.Invoke(message);

        /// <summary>Searches this account's catalogue. Never throws for Netflix problems: the outcome carries the reason.</summary>
        public async Task<NetflixSearchOutcome> SearchAsync(string term, CancellationToken ct)
        {
            if (IsPlaybackActive) return new(null, "Netflix is playing; search is paused until playback ends.", 0);
            if (_hub.BlockedFor(Owner) is { } blocked) return new(null, $"{blocked} Search is paused.", 0);

            await _hub.Gate.WaitAsync(ct);
            try
            {
                var failure = await EnsureReadyCoreAsync(ct);
                if (failure != null) return new(null, failure, 0);

                // One API call: POST /graphql SearchPageQueryResults (what Netflix's own search page sends).
                var query = NetflixSearchQuery.Load(QueryCachePath);
                var sw = Stopwatch.StartNew();
                var reply = NetflixSearchParser.Parse(await Browser.PostNetflixGraphQlAsync(query.BodyFor(term), ct));
                AddLog(reply.Ok ? $"API: {reply.Results.Count} results in {sw.ElapsedMilliseconds} ms (query {query.PersistedQueryId})"
                                : $"API error {reply.ErrorCode}: {reply.ErrorMessage}");

                // Netflix rotated the persisted query or changed its variables: take the current request from its own search
                // page, cache it, retry once.
                if (reply.NeedsNewTemplate)
                {
                    AddLog("Stored search request is out of date; capturing the current one from Netflix's search page...");
                    var capturedBody = await Browser.CaptureNetflixSearchRequestAsync(term, ct);
                    if (capturedBody == null)
                    {
                        return new(reply, $"Netflix rejected the stored search request ({reply.ErrorCode}) and a new one could not be captured.", sw.ElapsedMilliseconds);
                    }
                    await Browser.NavigateAsync(IdleUrl, ct); // capturing left it on the search page
                    query = new NetflixSearchQuery(capturedBody);
                    query.Save(QueryCachePath);
                    AddLog($"Captured and cached the current request (query {query.PersistedQueryId}).");
                    sw.Restart();
                    reply = NetflixSearchParser.Parse(await Browser.PostNetflixGraphQlAsync(query.BodyFor(term), ct));
                    AddLog(reply.Ok ? $"API: {reply.Results.Count} results in {sw.ElapsedMilliseconds} ms" : $"API error {reply.ErrorCode}: {reply.ErrorMessage}");
                }

                return reply.Ok
                    ? new(reply, null, sw.ElapsedMilliseconds)
                    : new(reply, $"Netflix search API error {reply.ErrorCode}: {reply.ErrorMessage}", sw.ElapsedMilliseconds);
            }
            finally
            {
                _hub.Gate.Release();
            }
        }

        /// <summary>
        /// Opens the browser if it is not open, and makes sure it is a signed-in member on a chosen profile. Returns null when
        /// ready, else the reason. Search and playback call it; it does the slow part only once per browser process.
        /// </summary>
        /// <param name="signInAgain">
        /// True when the viewer asked (a play): sign in again even if the last sign-in failed. Otherwise that failure is returned
        /// without touching Netflix.
        /// </param>
        public async Task<string?> EnsureReadyAsync(CancellationToken ct, bool signInAgain = false)
        {
            await _hub.Gate.WaitAsync(ct);
            try { return await EnsureReadyCoreAsync(ct, signInAgain); }
            finally { _hub.Gate.Release(); }
        }

        private async Task<string?> EnsureReadyCoreAsync(CancellationToken ct, bool signInAgain = false)
        {
            if (_hub.BlockedFor(Owner) is { } blocked) return blocked;

            if (Browser.IsRunning && _memberConfirmedPid == Browser.ProcessId)
            {
                if (_hub.PageOwner != Owner && !IsPlaybackActive)
                {
                    // Another service (Disney+) used the tab since: Netflix's search is a same-origin fetch, so be on netflix.com again.
                    await Browser.NavigateAsync(IdleUrl, ct);
                    AddLog("The tab was last used by another service; back on the Netflix idle page.");
                }
                _hub.PageOwner = Owner;
                return null;
            }

            if (_signInFailure is { } signInFailure)
            {
                if (!signInAgain) return signInFailure;
                _signInFailure = null;
                NeedsVerificationCode = false;
            }

            var sw = Stopwatch.StartNew();
            if (!Browser.IsRunning)
            {
                AddLog("Opening the Netflix browser (hidden behind Baird)...");
                var launchFailure = await _hub.EnsureRunningAsync(BrowseUrl, ct);
                if (launchFailure != null) return launchFailure;
            }
            else
            {
                await Browser.NavigateAsync(BrowseUrl, ct); // opened by something else (e.g. a spike tab): check from the start
            }
            _hub.PageOwner = Owner;

            // The search API also answers signed-out browsers, with the general country catalogue, which would not be
            // "filtered by this account": make sure this is a signed-in member on a chosen profile first.
            var failure = await EnsureMemberSessionAsync(ct);
            if (failure != null) return failure;
            _memberConfirmedPid = Browser.ProcessId;
            await Browser.NavigateAsync(IdleUrl, ct);
            AddLog($"Netflix browser ready in {sw.Elapsed.TotalSeconds:F1} s (signed in, profile chosen, waiting on {IdleUrl}).");
            return null;
        }

        /// <summary>
        /// Opens the browser in the background after <paramref name="delay"/> (let Baird's own start finish first), then keeps
        /// it open: every <see cref="WatchdogInterval"/>, if it has stopped and nothing is playing, it is opened again.
        /// </summary>
        public void Start(TimeSpan delay)
        {
            var ct = _lifetime.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, ct);
                    while (!ct.IsCancellationRequested)
                    {
                        // After a failed sign-in it waits for the viewer (a play): retrying could send them a code each time.
                        if (_hub.ActivePlayback == null && !NeedsSignIn && (!Browser.IsRunning || _memberConfirmedPid != Browser.ProcessId))
                        {
                            try
                            {
                                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                attempt.CancelAfter(TimeSpan.FromMinutes(2));
                                var failure = await EnsureReadyAsync(attempt.Token);
                                if (failure != null) AddLog($"Netflix browser not ready: {failure}");
                            }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                AddLog("Netflix browser not ready: gave up after 2 min; trying again later.");
                            }
                        }
                        await Task.Delay(WatchdogInterval, ct);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AddLog($"Netflix browser watchdog stopped: {ex.Message}"); }
            });
        }

        /// <summary>Signs in / picks a profile until the page reports a member. Returns null when ready, else the failure.</summary>
        private async Task<string?> EnsureMemberSessionAsync(CancellationToken ct)
        {
            const int maxProfileClicks = 3;
            bool signInTried = false;
            int profileClicks = 0;
            DateTime lastProfileClick = DateTime.MinValue;
            string? lastSeen = null;
            while (true)
            {
                await Task.Delay(PollInterval, ct);
                var state = await Browser.ReadNetflixSessionStateAsync(ct);
                var seen = state == null ? "no answer from the page"
                    : $"{state.Url} signedOut={state.SignedOut} member={state.IsMember} profiles={state.Profiles.Count}";
                if (seen != lastSeen) AddLog($"Session state: {seen}");
                lastSeen = seen;
                if (state == null) continue;

                if (state.SignedOut)
                {
                    if (Credentials == null)
                        return SignInFailed("Netflix is not signed in on this Baird, and no NETFLIX_USER/NETFLIX_PASS are in ~/.baird/config.ini.");
                    if (signInTried)
                        return SignInFailed("Netflix is not signed in on this Baird: signing in with the configured credentials did not work.");
                    signInTried = true;
                    AddLog($"Not signed in; signing in as {Credentials.User} (password not logged).");
                    await Browser.NavigateAsync(LoginUrl, ct);
                    var submit = await Browser.SubmitCredentialsAsync(Credentials.User, Credentials.Password, ct);
                    AddLog($"Sign-in: {submit.Detail}");
                    await Task.Delay(SettleDelay, ct);
                    if (await Browser.CheckAuthStatusAsync(ct) is { Kind: NetflixPageKind.MfaRequired } verify)
                    {
                        // Stay on this page: leaving it would throw away the code Netflix just sent.
                        AddLog($"Netflix asks for a verification code ({verify.Url}); the next play asks the viewer for it.");
                        NeedsVerificationCode = true;
                        return SignInFailed(VerificationCodeMessage);
                    }
                    await Browser.NavigateAsync(BrowseUrl, ct);
                    continue;
                }

                if (state.Profiles.Count > 0)
                {
                    // A click before Netflix's page is ready is ignored and the picker stays: click again after the settle time.
                    if (DateTime.UtcNow - lastProfileClick < SettleDelay) continue;
                    if (profileClicks == maxProfileClicks)
                        return $"Netflix's \"Who's watching?\" picker did not close after {maxProfileClicks} clicks.";
                    profileClicks++;
                    lastProfileClick = DateTime.UtcNow;
                    var chosen = await Browser.SelectNetflixProfileAsync(_profileName, ct);
                    AddLog($"\"Who's watching?\" shown ({string.Join(", ", state.Profiles)}); chose {chosen ?? "nothing"} (click {profileClicks})" +
                           (_profileName == null ? " (set NETFLIX_PROFILE in config.ini to choose)" : ""));
                    continue;
                }

                if (state.IsMember)
                {
                    AddLog("Signed in as a Netflix member.");
                    return null;
                }
            }
        }

        private string SignInFailed(string reason)
        {
            _signInFailure = reason;
            return reason;
        }

        /// <summary>
        /// Types the code the viewer was sent into Netflix's verification step. Null when Netflix took it and the browser is ready;
        /// else why not (<see cref="NeedsVerificationCode"/> stays true when the step is still showing, so the code can be typed again).
        /// </summary>
        public async Task<string?> SubmitVerificationCodeAsync(string code, CancellationToken ct)
        {
            await _hub.Gate.WaitAsync(ct);
            try
            {
                if (!NeedsVerificationCode) return _signInFailure ?? await EnsureReadyCoreAsync(ct);
                AddLog($"Sending the verification code ({code.Length} digits)."); // never the code itself
                if (!await Browser.SubmitMfaPinAsync(code, ct))
                {
                    NeedsVerificationCode = false;
                    return SignInFailed("Baird could not find where to type the code on Netflix's page.");
                }
                await Task.Delay(SettleDelay, ct);
                if (await Browser.CheckAuthStatusAsync(ct) is { Kind: NetflixPageKind.MfaRequired })
                {
                    AddLog("Netflix still shows its verification step: the code was not accepted.");
                    return CodeRefusedMessage;
                }
                AddLog("Netflix accepted the verification code.");
                NeedsVerificationCode = false;
                _signInFailure = null;
                return await EnsureReadyCoreAsync(ct);
            }
            finally { _hub.Gate.Release(); }
        }

        /// <summary>
        /// Shows Netflix's sign-in page on the screen, above Baird, for the VIEWER to sign in with their keyboard (Baird types
        /// nothing; a verification code goes into Netflix's page too), and waits until the page says they are signed in, they press
        /// Back (Escape), or <see cref="HandSignInTimeout"/> passes. Then Baird is put back on top and the browser gets ready as
        /// usual (profile, idle page). Null when signed in and ready, else why not, for the viewer.
        /// </summary>
        public async Task<string?> SignInByHandAsync(CancellationToken ct)
        {
            await _hub.Gate.WaitAsync(ct);
            try
            {
                if (_hub.BlockedFor(Owner) is { } blocked) return blocked;
                if (!Browser.IsRunning)
                {
                    var launchFailure = await _hub.EnsureRunningAsync(LoginUrl, ct);
                    if (launchFailure != null) return launchFailure;
                }
                _hub.PageOwner = Owner;
                IsPlaybackActive = true; // search and the other services stay away while the page is on screen
                try
                {
                    if (!await Browser.NavigateAsync(LoginUrl, ct)) return "The browser did not open Netflix's sign-in page.";
                    await Browser.RevealWindowAsync(ct);
                    AddLog("Netflix's sign-in page is on the screen for the viewer.");

                    var sw = Stopwatch.StartNew();
                    while (true)
                    {
                        if (sw.Elapsed >= HandSignInTimeout)
                        {
                            AddLog($"Nobody signed in to Netflix within {HandSignInTimeout.TotalMinutes:F0} minutes.");
                            return "Netflix sign-in took too long, so Baird came back.";
                        }
                        await Task.Delay(PollInterval, ct);
                        if (await Browser.WatchNetflixSignInAsync(ct) is not { } seen) continue; // navigating
                        if (seen.BackPressed)
                        {
                            AddLog("The viewer pressed Back on Netflix's sign-in page.");
                            return "Netflix sign-in cancelled.";
                        }
                        if (!seen.SignedOut && (seen.IsMember || seen.Profiles.Count > 0))
                        {
                            AddLog($"The viewer signed in to Netflix ({sw.Elapsed.TotalSeconds:F0} s).");
                            break;
                        }
                    }
                }
                finally
                {
                    using var hide = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await Browser.HideWindowAsync(hide.Token); } catch (Exception ex) { AddLog($"Could not put Baird back on top: {ex.Message}"); }
                    IsPlaybackActive = false;
                }

                _signInFailure = null;
                NeedsVerificationCode = false;
                _memberConfirmedPid = 0; // check the member and pick the profile from the start
                return await EnsureReadyCoreAsync(ct);
            }
            catch (OperationCanceledException) { return "Netflix sign-in cancelled."; }
            finally { _hub.Gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            await Browser.DisposeAsync();
        }
    }
}
