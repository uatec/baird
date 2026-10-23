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
            if (_browser is CdpBrowserService cdp) cdp.LogMessage += OnBrowserLog;
            SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync, this.WhenAnyValue(x => x.CanSearch));
            BackCommand = ReactiveCommand.Create(() => BackRequested?.Invoke(this, EventArgs.Empty));
        }

        /// <summary>
        /// False when the browser is shared (NetflixSession): it already writes its log to the journal once, so this screen
        /// only shows the browser's lines.
        /// </summary>
        public bool LogBrowserToJournal { get; set; } = true;

        private void OnBrowserLog(string message)
        {
            if (LogBrowserToJournal) AddLog(message);
            else AddScreenLog(message);
        }

        private void AddScreenLog(string message)
        {
            LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (LogEntries.Count > 200) LogEntries.RemoveAt(0);
        }

        public void AddLog(string message)
        {
            LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            Console.WriteLine($"[NetflixSearch] {message}"); // journal; never contains the password
            if (LogEntries.Count > 200) LogEntries.RemoveAt(0);
        }

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
            var session = new NetflixSession(_browser, _credentials, _profileName)
            {
                // Port 9223, as before: the spike starts its own browser each time and closes it afterwards.
                Port = 9223,
                ProfileDir = ProfileDir,
                QueryCachePath = QueryCachePath,
                PollInterval = PollInterval,
                SettleDelay = SettleDelay,
                IdleTimeout = null,
            };
            session.Log += AddLog;
            try
            {
                Status = $"Searching Netflix for \"{term}\"...";
                var outcome = await session.SearchAsync(term, cts.Token);
                if (!outcome.Ok)
                {
                    Status = $"FAILED: {outcome.Failure}";
                    AddLog(Status);
                    return;
                }

                var reply = outcome.Reply!;
                foreach (var r in reply.Results) Results.Add(r);
                Succeeded = Results.Count > 0;
                Status = Succeeded
                    ? $"Found {Results.Count} titles for \"{term}\" in this account's catalogue (Netflix API, {outcome.ElapsedMs} ms{(reply.HasNextPage ? ", more available" : "")})."
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
    }
}
