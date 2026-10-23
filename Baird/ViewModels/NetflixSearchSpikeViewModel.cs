using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Baird.Services;
using ReactiveUI;

namespace Baird.ViewModels
{
    /// <summary>
    /// Spike: prove Netflix search results can be retrieved from the signed-in session (so only titles available to this
    /// account/region come back). Uses the same hidden Chromium and profile as the Netflix playback spike, reads the search
    /// page, then closes the browser. Not integrated with Baird's own search yet.
    /// </summary>
    public class NetflixSearchSpikeViewModel : ReactiveObject
    {
        public const string DefaultSearchTerm = "One Piece";

        private readonly ICdpBrowserService _browser;
        private readonly NetflixCredentials? _credentials;
        private readonly string? _profileName;

        public event EventHandler? BackRequested;

        public ObservableCollection<NetflixSearchResult> Results { get; } = new();
        public ObservableCollection<string> LogEntries { get; } = new();

        private string _searchTerm = DefaultSearchTerm;
        public string SearchTerm { get => _searchTerm; set => this.RaiseAndSetIfChanged(ref _searchTerm, value); }

        private string _status = "Press Search to query Netflix with the signed-in session.";
        public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; private set { this.RaiseAndSetIfChanged(ref _isBusy, value); this.RaisePropertyChanged(nameof(CanSearch)); } }
        public bool CanSearch => !IsBusy;

        private bool _succeeded;
        public bool Succeeded { get => _succeeded; private set => this.RaiseAndSetIfChanged(ref _succeeded, value); }

        public string ProfileDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "baird", "browser-profile");

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
        /// <summary>Pause after submitting credentials / picking a profile before reopening the search page.</summary>
        public TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(4);

        public ReactiveCommand<Unit, Unit> SearchCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        public NetflixSearchSpikeViewModel(NetflixCredentials? credentials = null, string? profileName = null)
            : this(new CdpBrowserService(), credentials, profileName) { }

        public NetflixSearchSpikeViewModel(ICdpBrowserService browser, NetflixCredentials? credentials = null, string? profileName = null)
        {
            _browser = browser;
            _credentials = credentials;
            _profileName = profileName;
            if (_browser is CdpBrowserService cdp) cdp.LogMessage += AddLog;
            SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync, this.WhenAnyValue(x => x.CanSearch));
            BackCommand = ReactiveCommand.Create(() => BackRequested?.Invoke(this, EventArgs.Empty));
        }

        public void AddLog(string message)
        {
            LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            Console.WriteLine($"[NetflixSearch] {message}"); // journal; never contains the password
            if (LogEntries.Count > 200) LogEntries.RemoveAt(0);
        }

        /// <summary>Where a refreshed copy of Netflix's search request is cached (next to the browser profile).</summary>
        public string QueryCachePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "baird", "netflix-search-query.json");

        public async Task SearchAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            Succeeded = false;
            Results.Clear();
            var term = string.IsNullOrWhiteSpace(SearchTerm) ? DefaultSearchTerm : SearchTerm.Trim();
            using var cts = new CancellationTokenSource(Timeout);
            var ct = cts.Token;
            try
            {
                Status = $"Searching Netflix for \"{term}\"...";
                // Port 9223 so it never collides with the playback spike's 9222 (they share the profile, so not concurrently).
                if (!await _browser.LaunchAsync(_browser.FindBrowserExecutable(), "https://www.netflix.com/browse", 9223, ProfileDir, false, ct))
                {
                    Status = $"FAILED: could not start the browser: {_browser.LastError ?? "unknown error"} (is the Netflix spike still running?)";
                    return;
                }

                // 1. Make sure this is a signed-in member on a chosen profile: the search API also answers signed-out browsers,
                //    with the general country catalogue, which would not be "filtered by this account".
                var session = await EnsureMemberSessionAsync(ct);
                if (session != null) { Status = session; return; }

                // 2. One API call: POST /graphql SearchPageQueryResults (what Netflix's own search page sends).
                var query = NetflixSearchQuery.Load(QueryCachePath);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var reply = NetflixSearchParser.Parse(await _browser.PostNetflixGraphQlAsync(query.BodyFor(term), ct));
                AddLog(reply.Ok ? $"API: {reply.Results.Count} results in {sw.ElapsedMilliseconds} ms (query {query.PersistedQueryId})"
                                : $"API error {reply.ErrorCode}: {reply.ErrorMessage}");

                // 3. Netflix rotated the persisted query or changed its variables: take the current request from its own search
                //    page, cache it, retry once.
                if (reply.NeedsNewTemplate)
                {
                    AddLog("Stored search request is out of date; capturing the current one from Netflix's search page...");
                    var capturedBody = await _browser.CaptureNetflixSearchRequestAsync(term, ct);
                    if (capturedBody == null)
                    {
                        Status = $"FAILED: Netflix rejected the stored search request ({reply.ErrorCode}) and a new one could not be captured.";
                        return;
                    }
                    query = new NetflixSearchQuery(capturedBody);
                    query.Save(QueryCachePath);
                    AddLog($"Captured and cached the current request (query {query.PersistedQueryId}).");
                    sw.Restart();
                    reply = NetflixSearchParser.Parse(await _browser.PostNetflixGraphQlAsync(query.BodyFor(term), ct));
                    AddLog(reply.Ok ? $"API: {reply.Results.Count} results in {sw.ElapsedMilliseconds} ms" : $"API error {reply.ErrorCode}: {reply.ErrorMessage}");
                }

                if (!reply.Ok)
                {
                    Status = $"FAILED: Netflix search API error {reply.ErrorCode}: {reply.ErrorMessage}";
                    return;
                }

                foreach (var r in reply.Results) Results.Add(r);
                Succeeded = Results.Count > 0;
                Status = Succeeded
                    ? $"Found {Results.Count} titles for \"{term}\" in this account's catalogue (Netflix API, {sw.ElapsedMilliseconds} ms{(reply.HasNextPage ? ", more available" : "")})."
                    : $"No titles found for \"{term}\".";
                AddLog(Status);
            }
            catch (OperationCanceledException)
            {
                Status = $"FAILED: gave up after {Timeout.TotalSeconds:F0}s.";
                AddLog(Status);
            }
            catch (Exception ex)
            {
                Status = $"FAILED: {ex.Message}";
                AddLog(Status);
            }
            finally
            {
                try { await _browser.StopAsync(); } catch { }
                IsBusy = false;
            }
        }

        /// <summary>Signs in / picks a profile until the page reports a member. Returns null when ready, else a FAILED status.</summary>
        private async Task<string?> EnsureMemberSessionAsync(CancellationToken ct)
        {
            bool signInTried = false;
            string? profileChosen = null;
            while (true)
            {
                await Task.Delay(PollInterval, ct);
                var state = await _browser.ReadNetflixSessionStateAsync(ct);
                if (state == null) continue;

                if (state.SignedOut)
                {
                    if (_credentials == null)
                        return "FAILED: not signed in to Netflix and no NETFLIX_USER/NETFLIX_PASS in ~/.baird/config.ini.";
                    if (signInTried)
                        return "FAILED: not signed in to Netflix; signing in with the configured credentials did not work.";
                    signInTried = true;
                    AddLog($"Not signed in; signing in as {_credentials.User} (password not logged).");
                    await _browser.NavigateAsync("https://www.netflix.com/login", ct);
                    var submit = await _browser.SubmitCredentialsAsync(_credentials.User, _credentials.Password, ct);
                    AddLog($"Sign-in: {submit.Detail}");
                    await Task.Delay(SettleDelay, ct);
                    await _browser.NavigateAsync("https://www.netflix.com/browse", ct);
                    continue;
                }

                if (state.Profiles.Count > 0)
                {
                    if (profileChosen != null) continue; // still transitioning
                    profileChosen = await _browser.SelectNetflixProfileAsync(_profileName, ct);
                    AddLog($"\"Who's watching?\" shown ({string.Join(", ", state.Profiles)}); chose {profileChosen ?? "nothing"}" +
                           (_profileName == null ? " (set NETFLIX_PROFILE in config.ini to choose)" : ""));
                    await Task.Delay(SettleDelay, ct);
                    continue;
                }

                if (state.IsMember)
                {
                    AddLog("Signed in as a Netflix member.");
                    return null;
                }
            }
        }
    }
}
