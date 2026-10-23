using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>Result of one Disney+ search: the reply, or why there is none.</summary>
    public sealed record DisneySearchOutcome(DisneySearchReply? Reply, string? Failure, long ElapsedMs)
    {
        public bool Ok => Failure == null && Reply is { Ok: true };
    }

    /// <summary>
    /// Disney+ on Baird's shared browser (<see cref="BrowserHub"/>): the twin of <see cref="NetflixSession"/>. It attaches to the
    /// browser that is already open (or starts it), signs in and picks a profile once per browser process, then leaves the tab
    /// on <see cref="IdleUrl"/>: a page on Disney's origin with no video, so a covered Chromium is not decoding anything.
    /// Search and playback never run while the other service is playing.
    /// </summary>
    public sealed class DisneyService
    {
        public const string Owner = "Disney+";
        public const string IdleUrl = "https://www.disneyplus.com/robots.txt";

        private readonly BrowserHub _hub;
        private readonly DisneySession _session;
        private readonly string? _profileName;
        private int _signedInPid;

        public DisneyService(BrowserHub hub, DisneyCredentials? credentials, string? profileName = null)
        {
            _hub = hub;
            Credentials = credentials;
            _profileName = profileName;
            _session = new DisneySession(hub.Browser,
                (hub.Browser as ICdpPageDriver) ?? throw new ArgumentException("the browser must also implement ICdpPageDriver"),
                m => Log?.Invoke(m));
        }

        public BrowserHub Hub => _hub;
        public ICdpBrowserService Browser => _hub.Browser;
        public DisneyCredentials? Credentials { get; }
        public DisneySession Session => _session;

        public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromSeconds(60);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        public event Action<string>? Log;

        /// <summary>True while a Disney+ playback flow owns the browser; Netflix then stays away from the tab.</summary>
        public bool IsPlaybackActive
        {
            get => _hub.ActivePlayback == Owner;
            set { if (value) _hub.ActivePlayback = Owner; else if (_hub.ActivePlayback == Owner) _hub.ActivePlayback = null; }
        }

        /// <summary>Searches this account's catalogue. Never throws for Disney+ problems: the outcome carries the reason.</summary>
        public async Task<DisneySearchOutcome> SearchAsync(string term, CancellationToken ct)
        {
            if (IsPlaybackActive) return new(null, "Disney+ is playing; search is paused until playback ends.", 0);
            if (_hub.BlockedFor(Owner) is { } blocked) return new(null, $"{blocked} Search is paused.", 0);

            await _hub.Gate.WaitAsync(ct);
            try
            {
                var failure = await EnsureReadyCoreAsync(ct);
                if (failure != null) return new(null, failure, 0);

                var sw = Stopwatch.StartNew();
                var reply = await _session.SearchAsync(term, ct);
                Log?.Invoke(reply.Ok ? $"API: {reply.Results.Count} results in {sw.ElapsedMilliseconds} ms (incl. token capture)" : $"API error: {reply.ErrorMessage}");
                await Browser.NavigateAsync(IdleUrl, CancellationToken.None); // capturing left it on the search page
                return reply.Ok
                    ? new(reply, null, sw.ElapsedMilliseconds)
                    : new(reply, $"Disney+ search error: {reply.ErrorMessage}", sw.ElapsedMilliseconds);
            }
            finally
            {
                _hub.Gate.Release();
            }
        }

        /// <summary>
        /// Opens (or attaches to) the browser and makes sure Disney+ is signed in with a profile chosen. Returns null when
        /// ready, else the reason. Does the slow part once per browser process.
        /// </summary>
        /// <param name="beginPlayback">
        /// Mark Disney+ as playing before the gate is released, so the Netflix watchdog (which waits on the gate) cannot take the tab
        /// between "ready" and "playing". Only set when ready succeeds.
        /// </param>
        public async Task<string?> EnsureReadyAsync(CancellationToken ct, bool beginPlayback = false)
        {
            await _hub.Gate.WaitAsync(ct);
            try
            {
                var failure = await EnsureReadyCoreAsync(ct);
                if (failure == null && beginPlayback) IsPlaybackActive = true;
                return failure;
            }
            finally { _hub.Gate.Release(); }
        }

        private async Task<string?> EnsureReadyCoreAsync(CancellationToken ct)
        {
            if (_hub.BlockedFor(Owner) is { } blocked) return blocked;

            if (Browser.IsRunning && _signedInPid == Browser.ProcessId)
            {
                if (_hub.PageOwner != Owner && !IsPlaybackActive)
                {
                    await Browser.NavigateAsync(IdleUrl, ct);
                    Log?.Invoke("The tab was last used by another service; back on the Disney+ idle page.");
                }
                _hub.PageOwner = Owner;
                return null;
            }

            var sw = Stopwatch.StartNew();
            if (!Browser.IsRunning)
            {
                Log?.Invoke("Opening the browser (hidden behind Baird)...");
                var launchFailure = await _hub.EnsureRunningAsync(DisneySearchParser.SearchPageUrl, ct);
                if (launchFailure != null) return launchFailure;
            }
            else
            {
                Log?.Invoke("Using the browser that is already open.");
                await Browser.NavigateAsync(DisneySearchParser.SearchPageUrl, ct);
            }
            _hub.PageOwner = Owner;

            var problem = await _session.EnsureSignedInAsync(Credentials, _profileName, SignInTimeout, PollInterval, ct);
            if (problem != null) return problem;

            _signedInPid = Browser.ProcessId;
            await Browser.NavigateAsync(IdleUrl, ct);
            Log?.Invoke($"Disney+ ready in {sw.Elapsed.TotalSeconds:F1} s (signed in, profile chosen, waiting on {IdleUrl}).");
            return null;
        }
    }
}
