using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

        public async Task SearchAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            Succeeded = false;
            Results.Clear();
            var term = string.IsNullOrWhiteSpace(SearchTerm) ? DefaultSearchTerm : SearchTerm.Trim();
            var url = NetflixSearchParser.SearchUrl(term);
            using var cts = new CancellationTokenSource(Timeout);
            var ct = cts.Token;
            try
            {
                Status = $"Searching Netflix for \"{term}\"...";
                AddLog($"Searching for \"{term}\": {url}");
                // Port 9223 so it never collides with the playback spike's 9222 (they share the profile, so not concurrently).
                if (!await _browser.LaunchAsync(_browser.FindBrowserExecutable(), url, 9223, ProfileDir, false, ct))
                {
                    Status = $"FAILED: could not start the browser: {_browser.LastError ?? "unknown error"} (is the Netflix spike still running?)";
                    return;
                }

                bool signInTried = false;
                string? profileChosen = null;
                int lastCount = -1, stablePolls = 0;
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(PollInterval, ct);
                    var page = await _browser.ReadNetflixSearchPageAsync(ct);
                    if (page == null) continue;

                    if (page.SignedOut)
                    {
                        if (_credentials == null || signInTried)
                        {
                            Status = _credentials == null
                                ? "FAILED: not signed in to Netflix and no NETFLIX_USER/NETFLIX_PASS in ~/.baird/config.ini."
                                : "FAILED: not signed in to Netflix; signing in with the configured credentials did not work.";
                            return;
                        }
                        signInTried = true;
                        AddLog($"Not signed in; signing in as {_credentials.User} (password not logged).");
                        await _browser.NavigateAsync("https://www.netflix.com/login", ct);
                        var submit = await _browser.SubmitCredentialsAsync(_credentials.User, _credentials.Password, ct);
                        AddLog($"Sign-in: {submit.Detail}");
                        await Task.Delay(SettleDelay, ct);
                        await _browser.NavigateAsync(url, ct);
                        continue;
                    }

                    if (page.Profiles.Count > 0)
                    {
                        if (profileChosen != null) continue; // still transitioning
                        profileChosen = await _browser.SelectNetflixProfileAsync(_profileName, ct);
                        AddLog($"\"Who's watching?\" shown ({string.Join(", ", page.Profiles)}); chose {profileChosen ?? "nothing"}" +
                               (_profileName == null ? " (set NETFLIX_PROFILE in config.ini to choose)" : ""));
                        await Task.Delay(SettleDelay, ct);
                        await _browser.NavigateAsync(url, ct);
                        continue;
                    }

                    // Results render progressively: wait until the card count stops changing.
                    if (page.Cards.Count > 0 && page.Cards.Count == lastCount) stablePolls++; else stablePolls = 0;
                    lastCount = page.Cards.Count;
                    if (stablePolls >= 2)
                    {
                        foreach (var r in NetflixSearchParser.ToResults(page.Cards)) Results.Add(r);
                        Succeeded = Results.Count > 0;
                        Status = Succeeded
                            ? $"Found {Results.Count} titles for \"{term}\" in this account's catalogue."
                            : $"FAILED: Netflix showed {page.Cards.Count} cards but none had a readable title/id.";
                        AddLog(Status);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Status = $"FAILED: no search results within {Timeout.TotalSeconds:F0}s.";
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
    }
}
