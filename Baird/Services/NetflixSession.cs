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
    /// profile and Chromium locks a profile to one process. Search keeps the browser open (hidden behind Baird) so the next
    /// search is one API call; it stops after <see cref="IdleTimeout"/> without use. A playback flow relaunches the same
    /// browser at the watch URL and stops it when it ends.
    /// </summary>
    public sealed class NetflixSession : IAsyncDisposable
    {
        public const int CdpPort = 9222;
        public const string BrowseUrl = "https://www.netflix.com/browse";

        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly string? _profileName;
        private int _memberConfirmedPid;
        private CancellationTokenSource? _idleCts;

        public ICdpBrowserService Browser { get; }
        public NetflixCredentials? Credentials { get; }

        /// <summary>True while a playback flow owns the browser; search then answers with a failure instead of disturbing it.</summary>
        public bool IsPlaybackActive { get; set; }

        public int Port { get; set; } = CdpPort;
        public string ProfileDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "baird", "browser-profile");
        /// <summary>Where a refreshed copy of Netflix's search request is cached (next to the browser profile).</summary>
        public string QueryCachePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "baird", "netflix-search-query.json");
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
        /// <summary>Pause after submitting credentials / picking a profile before reading the page again.</summary>
        public TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(4);
        /// <summary>Stop the hidden browser after this long without a search. Null: never (the caller stops it).</summary>
        public TimeSpan? IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

        public event Action<string>? Log;

        public NetflixSession(ICdpBrowserService browser, NetflixCredentials? credentials, string? profileName = null)
        {
            Browser = browser;
            Credentials = credentials;
            _profileName = profileName;
        }

        private void AddLog(string message) => Log?.Invoke(message);

        /// <summary>Searches this account's catalogue. Never throws for Netflix problems: the outcome carries the reason.</summary>
        public async Task<NetflixSearchOutcome> SearchAsync(string term, CancellationToken ct)
        {
            if (IsPlaybackActive) return new(null, "Netflix is playing; search is paused until playback ends.", 0);

            await _lock.WaitAsync(ct);
            try
            {
                _idleCts?.Cancel();

                if (!Browser.IsRunning || _memberConfirmedPid != Browser.ProcessId)
                {
                    if (!Browser.IsRunning && !await Browser.LaunchAsync(Browser.FindBrowserExecutable(), BrowseUrl, Port, ProfileDir, false, ct))
                    {
                        return new(null, $"Could not start the browser: {Browser.LastError ?? "unknown error"}", 0);
                    }

                    // The search API also answers signed-out browsers, with the general country catalogue, which would not be
                    // "filtered by this account": make sure this is a signed-in member on a chosen profile first.
                    var failure = await EnsureMemberSessionAsync(ct);
                    if (failure != null) return new(null, failure, 0);
                    _memberConfirmedPid = Browser.ProcessId;
                }

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
                ScheduleIdleStop();
                _lock.Release();
            }
        }

        private void ScheduleIdleStop()
        {
            if (IdleTimeout is not { } timeout) return;
            _idleCts?.Cancel();
            var cts = _idleCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(timeout, cts.Token);
                    if (IsPlaybackActive || !Browser.IsRunning) return;
                    AddLog($"No Netflix search for {timeout.TotalMinutes:F0} min; stopping the hidden browser.");
                    await Browser.StopAsync();
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { AddLog($"Idle stop failed: {ex.Message}"); }
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
                        return "Not signed in to Netflix and no NETFLIX_USER/NETFLIX_PASS in ~/.baird/config.ini.";
                    if (signInTried)
                        return "Not signed in to Netflix; signing in with the configured credentials did not work.";
                    signInTried = true;
                    AddLog($"Not signed in; signing in as {Credentials.User} (password not logged).");
                    await Browser.NavigateAsync("https://www.netflix.com/login", ct);
                    var submit = await Browser.SubmitCredentialsAsync(Credentials.User, Credentials.Password, ct);
                    AddLog($"Sign-in: {submit.Detail}");
                    await Task.Delay(SettleDelay, ct);
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

        public async ValueTask DisposeAsync()
        {
            _idleCts?.Cancel();
            await Browser.DisposeAsync();
        }
    }
}
