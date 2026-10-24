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

        private readonly DisneyService _service;
        private readonly bool _shared;

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

        public string ProfileDir { get => _service.Hub.ProfileDir; set => _service.Hub.ProfileDir = value; }

        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);
        public TimeSpan SignInTimeout { get => _service.SignInTimeout; set => _service.SignInTimeout = value; }
        public TimeSpan PollInterval { get => _service.PollInterval; set => _service.PollInterval = value; }

        /// <summary>True for Baird's shared browser, which stays open after a search; false closes the browser (a Disney+-only browser).</summary>
        public bool KeepBrowserOpen { get; set; }

        public ReactiveCommand<Unit, Unit> SearchCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        public DisneySearchSpikeViewModel(DisneyCredentials? credentials = null, string? profileName = null)
            : this(new CdpBrowserService(), credentials, profileName) { }

        /// <summary>A Disney+-only browser (tests, no shared browser). It closes the browser after each search.</summary>
        public DisneySearchSpikeViewModel(ICdpBrowserService browser, DisneyCredentials? credentials = null, string? profileName = null)
            : this(new DisneyService(new BrowserHub(browser) { Port = 9225 }, credentials, profileName), shared: false)
        {
            if (browser is CdpBrowserService cdp) cdp.LogMessage += AddLog;
        }

        /// <summary>Disney+ on Baird's shared browser. The service's log reaches the journal once, from where it was created.</summary>
        public DisneySearchSpikeViewModel(DisneyService service) : this(service, shared: true) { KeepBrowserOpen = true; }

        private DisneySearchSpikeViewModel(DisneyService service, bool shared)
        {
            _service = service;
            _shared = shared;
            _service.Log += shared ? AddScreenLog : AddLog; // shared: the service's lines reach the journal once, from where it was created
            SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync, this.WhenAnyValue(x => x.CanSearch));
            BackCommand = ReactiveCommand.Create(() => BackRequested?.Invoke(this, EventArgs.Empty));
        }

        private void AddScreenLog(string message)
        {
            LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (LogEntries.Count > 200) LogEntries.RemoveAt(0);
        }

        public void AddLog(string message)
        {
            AddScreenLog(message);
            Console.WriteLine($"[DisneySearch] {message}"); // journal; never contains the password or the token
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
                var outcome = await _service.SearchAsync(term, ct);
                if (!outcome.Ok)
                {
                    var failure = outcome.Failure ?? "no reply";
                    Status = failure.StartsWith("FAILED", StringComparison.Ordinal) ? failure : "FAILED: " + failure;
                    AddLog(Status);
                    return;
                }

                foreach (var r in outcome.Reply!.Results) Results.Add(r);
                Succeeded = Results.Count > 0;
                Status = Succeeded
                    ? $"Found {Results.Count} titles for \"{term}\" in this account's Disney+ catalogue ({outcome.ElapsedMs} ms)."
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
                if (!KeepBrowserOpen) { try { await _service.Browser.StopAsync(); } catch { } }
                IsBusy = false;
            }
        }
    }
}
