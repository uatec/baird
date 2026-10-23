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
    /// Spike: prove Disney+ search results can be retrieved from the signed-in session, so only titles available to this
    /// account, profile and region come back. Starts the hidden Chromium on the shared profile, signs in and picks a profile if
    /// needed, calls Disney's explore search API with the web app's own token, then closes the browser. Not integrated with
    /// Baird's own search yet.
    /// </summary>
    public class DisneySearchSpikeViewModel : ReactiveObject
    {
        public const string DefaultSearchTerm = "Mandalorian";

        private readonly ICdpBrowserService _browser;
        private readonly DisneySession _session;
        private readonly DisneyCredentials? _credentials;
        private readonly string? _profileName;

        public event EventHandler? BackRequested;

        public ObservableCollection<DisneySearchResult> Results { get; } = new();
        public ObservableCollection<string> LogEntries { get; } = new();

        private string _searchTerm = DefaultSearchTerm;
        public string SearchTerm { get => _searchTerm; set => this.RaiseAndSetIfChanged(ref _searchTerm, value); }

        private string _status = "Press Search to query Disney+ with the signed-in session.";
        public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; private set { this.RaiseAndSetIfChanged(ref _isBusy, value); this.RaisePropertyChanged(nameof(CanSearch)); } }
        public bool CanSearch => !IsBusy;

        private bool _succeeded;
        public bool Succeeded { get => _succeeded; private set => this.RaiseAndSetIfChanged(ref _succeeded, value); }

        public string ProfileDir { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "baird", "browser-profile");

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);
        public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromSeconds(60);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        public ReactiveCommand<Unit, Unit> SearchCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        public DisneySearchSpikeViewModel(DisneyCredentials? credentials = null, string? profileName = null)
            : this(new CdpBrowserService(), credentials, profileName) { }

        public DisneySearchSpikeViewModel(ICdpBrowserService browser, DisneyCredentials? credentials = null, string? profileName = null)
        {
            _browser = browser;
            _credentials = credentials;
            _profileName = profileName;
            if (_browser is CdpBrowserService cdp) cdp.LogMessage += AddLog;
            _session = new DisneySession(browser, (browser as ICdpPageDriver) ?? throw new ArgumentException("browser must also implement ICdpPageDriver"), AddLog);
            SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync, this.WhenAnyValue(x => x.CanSearch));
            BackCommand = ReactiveCommand.Create(() => BackRequested?.Invoke(this, EventArgs.Empty));
        }

        public void AddLog(string message)
        {
            LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            Console.WriteLine($"[DisneySearch] {message}"); // journal; never contains the password or the token
            if (LogEntries.Count > 200) LogEntries.RemoveAt(0);
        }

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
                Status = $"Searching Disney+ for \"{term}\"...";
                // Port 9225: the Netflix spikes use 9222 and 9223, the Disney playback spike 9224. The profile is shared, so never at the same time.
                if (!await _browser.LaunchAsync(_browser.FindBrowserExecutable(), DisneySearchParser.SearchPageUrl, 9225, ProfileDir, false, ct))
                {
                    Status = $"FAILED: could not start the browser: {_browser.LastError ?? "unknown error"} (is another spike still running?)";
                    return;
                }

                var problem = await _session.EnsureSignedInAsync(_credentials, _profileName, SignInTimeout, PollInterval, ct);
                if (problem != null) { Status = problem; AddLog(problem); return; }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var reply = await _session.SearchAsync(term, ct);
                AddLog(reply.Ok ? $"API: {reply.Results.Count} results in {sw.ElapsedMilliseconds} ms (incl. token capture)" : $"API error: {reply.ErrorMessage}");
                if (!reply.Ok)
                {
                    Status = $"FAILED: Disney+ search error: {reply.ErrorMessage}";
                    return;
                }

                foreach (var r in reply.Results) Results.Add(r);
                Succeeded = Results.Count > 0;
                Status = Succeeded
                    ? $"Found {Results.Count} titles for \"{term}\" in this account's Disney+ catalogue ({sw.ElapsedMilliseconds} ms)."
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
