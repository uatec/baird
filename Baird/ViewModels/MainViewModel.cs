using ReactiveUI;
using System.Runtime.InteropServices;
using Baird.Services;
using Baird.Models; // For potentially reused types, though ScreensaverViewModel handles it.

namespace Baird.ViewModels
{
    public class MainViewModel : ReactiveObject
    {
        private bool _isEpgActive;
        public bool IsEpgActive
        {
            get => _isEpgActive;
            set => this.RaiseAndSetIfChanged(ref _isEpgActive, value);
        }

        private bool _isVideoHudVisible;
        public bool IsVideoHudVisible
        {
            get => _isVideoHudVisible;
            set => this.RaiseAndSetIfChanged(ref _isVideoHudVisible, value);
        }

        private bool _isSubtitlesEnabled;
        public bool IsSubtitlesEnabled
        {
            get => _isSubtitlesEnabled;
            set => this.RaiseAndSetIfChanged(ref _isSubtitlesEnabled, value);
        }

        private bool _isPaused;
        public bool IsPaused
        {
            get => _isPaused;
            set => this.RaiseAndSetIfChanged(ref _isPaused, value);
        }



        public OmniSearchViewModel OmniSearch { get; }
        public HistoryViewModel History { get; }
        public WatchlistViewModel Watchlist { get; }
        public TabNavigationViewModel MainMenu { get; }

        private MediaItem? _activeItem;
        public MediaItem? ActiveItem
        {
            get => _activeItem;
            set => this.RaiseAndSetIfChanged(ref _activeItem, value);
        }

        private string _appVersion = "v0.0.0";
        public string AppVersion
        {
            get => _appVersion;
            set => this.RaiseAndSetIfChanged(ref _appVersion, value);
        }

        private string _currentVersion = "v0.0.0";
        private readonly VersionCheckService _versionCheckService;
        // Actually MainViewModel uses HistoryService in PlayItem.
        // Let's keep a private reference to DataService.
        private readonly IDataService _dataService;

        // Track current episode list for auto-play next episode
        private System.Collections.Generic.List<MediaItem>? _currentEpisodeList;

        // Track parent show/season context for season transitions
        private string? _currentShowId;  // The base show ID (without season suffix)
        private string? _currentSeasonId;  // The current season ID (with season suffix if applicable)

        public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SelectNextChannelCommand { get; }
        public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SelectPreviousChannelCommand { get; }

        public ScreensaverViewModel Screensaver { get; }

        public MainViewModel(IDataService dataService, ISearchHistoryService searchHistoryService, ScreensaverService screensaverService)
        {
            _dataService = dataService;
            _versionCheckService = new VersionCheckService();
            Screensaver = new ScreensaverViewModel(screensaverService);
            // HistoryService = historyService; // Remove property? 
            // VideoPlayer layer needs HistoryService? 
            // MainView sets vLayer.HistoryService.
            // We need to exposing IHistoryService or IDataService to View?
            // User asked to encapsulate.
            // But if VideoPlayer needs HistoryService...
            // Let's check MainView again. 
            // MainView created HistoryService and passed it to VM AND VideoLayer.
            // Now MainView will create DataService.
            // VideoLayer likely needs updates too.

            SelectNextChannelCommand = ReactiveCommand.Create(SelectNextChannel);
            SelectPreviousChannelCommand = ReactiveCommand.Create(SelectPreviousChannel);

            OmniSearch = new OmniSearchViewModel(dataService, searchHistoryService, () => AllChannels);
            History = new HistoryViewModel(dataService);
            Watchlist = new WatchlistViewModel(dataService);

            History.PlayRequested += (s, item) => PlayItem(item);
            History.BackRequested += (s, e) => GoBack();

            Watchlist.PlayRequested += (s, item) => PlayItem(item);
            Watchlist.BackRequested += (s, e) => GoBack();

            // Subscribe to history updates to keep HistoryViewModel in sync
            _dataService.HistoryUpdated += async (s, e) =>
            {
                try
                {
                    // Refresh history view when history is updated (video watched/resumed)
                    await History.RefreshAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainViewModel] Error refreshing history after update: {ex.Message}");
                }
            };

            // Auto-refresh Watchlist?
            // WatchlistViewModel subscribes to WatchlistUpdated internally. 
            // But we might want initial load.
            // Let's add initial load in attached logic or here.
            // History is loaded in AttachedToVisualTree in MainView.
            // We should do the same for Watchlist.

            IsVideoHudVisible = true;

            IsSubtitlesEnabled = NativeUtils.GetCapsLockState();

            // ProgrammeChildren = new System.Collections.ObjectModel.ObservableCollection<Baird.Services.MediaItem>();

            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            _currentVersion = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v0.0.0";
            AppVersion = _currentVersion;

            // Initialize HUD Timer
            _hudTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = System.TimeSpan.FromSeconds(5)
            };
            _hudTimer.Tick += (s, e) =>
            {
                IsVideoHudVisible = false;
                _hudTimer.Stop();
            };
            // Start it initially so it hides after 5s of startup
            _hudTimer.Start();

            // Initialize Version Check Timer
            _versionCheckTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = System.TimeSpan.FromSeconds(30)
            };
            _versionCheckTimer.Tick += async (s, e) => await CheckForUpdatesAsync();
            _versionCheckTimer.Start();

            // Check immediately on startup
            _ = CheckForUpdatesAsync();

            OmniSearch.PlayRequested += (s, item) => PlayItem(item);
            OmniSearch.BackRequested += (s, e) => GoBack();

            // Create MainMenu with History and Search tabs
            var tabs = new[]
            {
                new TabItem("History", History),
                new TabItem("Search", OmniSearch),
                new TabItem("Watchlist", Watchlist),
            };
            MainMenu = new TabNavigationViewModel(tabs);
            MainMenu.BackRequested += (s, e) => GoBack();
        }

        private Avalonia.Threading.DispatcherTimer _hudTimer;
        private Avalonia.Threading.DispatcherTimer _versionCheckTimer;
        // private readonly System.Collections.Generic.IEnumerable<Baird.Services.IMediaProvider> _providers; // Removed

        private async System.Threading.Tasks.Task CheckForUpdatesAsync()
        {
            try
            {
                var latestVersion = await _versionCheckService.GetLatestVersionAsync();

                if (latestVersion != null && !string.IsNullOrEmpty(latestVersion))
                {
                    // Normalize versions for comparison (remove 'v' prefix if present)
                    var currentVersionStr = _currentVersion.TrimStart('v');
                    var latestVersionStr = latestVersion.TrimStart('v');

                    // Simple string comparison (works for semantic versioning)
                    if (latestVersionStr != currentVersionStr)
                    {
                        AppVersion = $"{_currentVersion} (v{latestVersionStr} is available)";
                        Console.WriteLine($"[MainViewModel] Update available: {latestVersionStr}");
                    }
                    else
                    {
                        AppVersion = _currentVersion;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainViewModel] Error checking for updates: {ex.Message}");
            }
        }

        public void ResetHudTimer()
        {
            IsVideoHudVisible = true;
            _hudTimer.Stop();
            _hudTimer.Start();
        }

        public void PlayItem(MediaItem item)
        {
            if (item.Type == MediaType.Brand)
            {
                // Clear episode list and context when opening a programme (not playing an episode)
                _currentEpisodeList = null;
                _currentShowId = null;
                _currentSeasonId = null;
                OpenProgramme(item);
                return;
            }

            // Clear episode list for media items
            // (will be set after by OpenProgramme.PlayRequested if playing from programme details)
            _currentEpisodeList = null;
            _currentShowId = null;
            _currentSeasonId = null;

            if (!string.IsNullOrEmpty(item.StreamUrl))
            {
                // Check for resume progress
                var history = _dataService.GetHistory(item.Id);
                TimeSpan? resumeTime = null;

                if (history != null && !history.IsFinished && !item.IsLive)
                {
                    // Resume logic
                    resumeTime = history.LastPosition;
                    Console.WriteLine($"[MainViewModel] Resuming {item.Name} at {resumeTime}");
                }

                ActivateChannel(item, resumeTime);

                // Push ShowingVideoPlayerViewModel to navigation stack
                // This represents "showing video player" as a proper page
                PushViewModel(new ShowingVideoPlayerViewModel());
            }
        }

        public void GoBack()
        {
            // Pop the current page to go back
            // This works for ShowingVideoPlayerViewModel too - it will pop back to the previous page
            PopViewModel();
        }

        public Stack<ReactiveObject> NavigationHistory { get; } = new();

        private ReactiveObject? _currentPage;
        public ReactiveObject? CurrentPage
        {
            get => _currentPage;
            private set
            {
                this.RaiseAndSetIfChanged(ref _currentPage, value);
                this.RaisePropertyChanged(nameof(IsVideoPlayerActive));
            }
        }

        public bool IsVideoPlayerActive => CurrentPage is ShowingVideoPlayerViewModel || CurrentPage == null;

        public void PushViewModel(ReactiveObject viewModel)
        {
            // Deduplicate ShowingVideoPlayerViewModel - if we're pushing one and the top of the stack
            // is already ShowingVideoPlayerViewModel, replace it instead of stacking
            // This prevents back navigation from going through each episode when auto-playing
            if (viewModel is ShowingVideoPlayerViewModel &&
                NavigationHistory.Count > 0 &&
                NavigationHistory.Peek() is ShowingVideoPlayerViewModel)
            {
                Console.WriteLine($"[Navigation] Replacing existing ShowingVideoPlayerViewModel");
                NavigationHistory.Pop();
            }

            Console.WriteLine($"[Navigation] Pushing {viewModel.GetType().Name}");
            NavigationHistory.Push(viewModel);
            CurrentPage = viewModel;
        }

        public void PopViewModel()
        {
            if (NavigationHistory.Count > 0)
            {
                NavigationHistory.Pop();
                CurrentPage = NavigationHistory.Count > 0 ? NavigationHistory.Peek() : null;
                Console.WriteLine($"[Navigation] Popping to {CurrentPage?.GetType().Name}");
            }
            else
            {
                Console.WriteLine("[Navigation] No pages to pop");
                CurrentPage = null;
            }
        }

        private MediaItem? FindNextEpisode(MediaItem currentEpisode)
        {
            if (_currentEpisodeList == null || _currentEpisodeList.Count == 0)
                return null;

            var currentIndex = _currentEpisodeList.FindIndex(e => e.Id == currentEpisode.Id);
            if (currentIndex == -1 || currentIndex >= _currentEpisodeList.Count - 1)
                return null; // Not found or last episode

            return _currentEpisodeList[currentIndex + 1];
        }

        // Extract the next season ID from a current season ID
        // Format: "showId|seasonNumber" -> returns "showId|nextSeasonNumber"
        private string? GetNextSeasonId(string? currentSeasonId)
        {
            if (string.IsNullOrEmpty(currentSeasonId) || !currentSeasonId.Contains("|"))
                return null;

            var parts = currentSeasonId.Split('|');
            if (parts.Length != 2)
                return null;

            var showId = parts[0];
            if (int.TryParse(parts[1], out int currentSeasonNum))
            {
                var nextSeasonNum = currentSeasonNum + 1;
                return $"{showId}|{nextSeasonNum}";
            }

            return null;
        }

        // Try to load the next season and return its first episode
        private async Task<MediaItem?> TryLoadNextSeasonFirstEpisode()
        {
            if (string.IsNullOrEmpty(_currentSeasonId))
            {
                Console.WriteLine("[MainViewModel] No current season ID, cannot load next season");
                return null;
            }

            var nextSeasonId = GetNextSeasonId(_currentSeasonId);
            if (nextSeasonId == null)
            {
                Console.WriteLine("[MainViewModel] Could not determine next season ID");
                return null;
            }

            Console.WriteLine($"[MainViewModel] Attempting to load next season: {nextSeasonId}");

            try
            {
                var nextSeasonEpisodes = await _dataService.GetChildrenAsync(nextSeasonId);
                var episodeList = nextSeasonEpisodes.Where(e => e.Type != MediaType.Brand && e.Type != MediaType.Folder).ToList();

                if (episodeList.Count == 0)
                {
                    Console.WriteLine($"[MainViewModel] Next season {nextSeasonId} has no episodes");
                    return null;
                }

                // Update context for the new season
                _currentSeasonId = nextSeasonId;
                _currentEpisodeList = episodeList;
                Console.WriteLine($"[MainViewModel] Loaded {episodeList.Count} episodes from next season");

                return episodeList[0];
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainViewModel] Failed to load next season {nextSeasonId}: {ex.Message}");
                return null;
            }
        }

        public async Task PlayNextEpisodeOrGoBack()
        {
            if (ActiveItem == null)
            {
                PopViewModel();
                return;
            }

            // Convert ActiveItem to MediaItem for searching
            var currentItem = new MediaItem
            {
                Id = ActiveItem.Id,
                Name = ActiveItem.Name,
                Details = ActiveItem.Details,
                ImageUrl = ActiveItem.ImageUrl,
                Source = ActiveItem.Source,
                Type = ActiveItem.Type,
                Synopsis = ActiveItem.Synopsis,
                Subtitle = ActiveItem.Subtitle,
                StreamUrl = ActiveItem.StreamUrl,
                IsLive = ActiveItem.IsLive,
                ChannelNumber = ActiveItem.ChannelNumber
            };

            var nextEpisode = FindNextEpisode(currentItem);
            if (nextEpisode != null)
            {
                Console.WriteLine($"[MainViewModel] Auto-playing next episode in current season: {nextEpisode.Name}");
                PlayItem(nextEpisode);
            }
            else
            {
                // No more episodes in current season, try next season
                Console.WriteLine("[MainViewModel] No next episode in current season, checking for next season");
                var nextSeasonEpisode = await TryLoadNextSeasonFirstEpisode();

                if (nextSeasonEpisode != null)
                {
                    Console.WriteLine($"[MainViewModel] Auto-playing first episode of next season: {nextSeasonEpisode.Name}");
                    PlayItem(nextSeasonEpisode);
                }
                else
                {
                    Console.WriteLine("[MainViewModel] No next season, navigating back");
                    PopViewModel();
                }
            }
        }

        public System.Collections.Generic.List<Baird.Services.MediaItem> AllChannels { get; private set; } = new();

        public async System.Threading.Tasks.Task RefreshChannels()
        {
            try
            {
                var results = await _dataService.GetListingAsync(); // Replaces tasks loop

                AllChannels = results
                    .Where(i => i.IsLive)
                    .Where(i => i.ChannelNumber != null && i.ChannelNumber != "0")
                    .OrderBy(i =>
                    {
                        if (i.ChannelNumber == null) return int.MaxValue;
                        if (int.TryParse(i.ChannelNumber, out var num)) return num;
                        return int.MaxValue;
                    })
                    .ToList();
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"[MainViewModel] Error refreshing channels: {ex.Message}");
            }
        }

        public void SelectNextChannel()
        {
            Console.WriteLine($"[MainViewModel] SelectNextChannel: ActiveItem={ActiveItem?.Name}, AllChannels.Count={AllChannels.Count}");
            if (ActiveItem == null || AllChannels.Count == 0) return;

            var currentIndex = AllChannels.FindIndex(c => c.Id == ActiveItem.Id);
            if (currentIndex == -1)
            {
                // Current item not in list (maybe VOD or Brand), jump to first channel?
                // Or do nothing? Let's jump to first channel for now.
                if (AllChannels.Count > 0) ActivateChannel(AllChannels[0]);
                return;
            }

            var nextIndex = (currentIndex + 1) % AllChannels.Count;
            ActivateChannel(AllChannels[nextIndex]);
        }

        public void SelectPreviousChannel()
        {
            Console.WriteLine($"[MainViewModel] SelectPreviousChannel: ActiveItem={ActiveItem?.Name}, AllChannels.Count={AllChannels.Count}");
            if (ActiveItem == null || AllChannels.Count == 0) return;

            var currentIndex = AllChannels.FindIndex(c => c.Id == ActiveItem.Id);
            if (currentIndex == -1)
            {
                if (AllChannels.Count > 0) ActivateChannel(AllChannels[0]);
                return;
            }

            var prevIndex = (currentIndex - 1 + AllChannels.Count) % AllChannels.Count;
            ActivateChannel(AllChannels[prevIndex]);
        }

        // Helper to activate a channel (similar to PlayItem in View, but VM based)
        // Note: Actual playback starts because ActiveItem is bound in View.
        private void ActivateChannel(Baird.Services.MediaItem item, TimeSpan? resumeTime = null)
        {
            ActiveItem = item;
            // ActiveItem = new ActiveMedia... 
            // We use MediaItem directly now.
            // If resumeTime is needed, user might use a global property or pass it to player differently.
            // "VideoPlayer.SetCurrentMediaItem(mediaItem)" handles it.
            // BUT wait. Previously MainView.axaml.cs subscribed to ActiveItem changes and called SetCurrentMediaItem.
            // How does it pass resumeTime?
            // "VideoPlayer.SetCurrentMediaItem" might check History itself?
            // Or MainViewModel logic needs to ensure the item has history attached?
            // Since we use DataService and calling GetHistory, we should Attach history to the item if not present.
            // The item passed to ActiveItem should have History populated if we want the player to see it?
            // But History is now on MediaItem.History property.
            // So if `item` has `History` populated with `LastPosition`, the player can read it.

            // Wait, I fetched `history` in `PlayItem`.
            // I should assign it to `item.History` before setting `ActiveItem`?
            // Yes.
            if (resumeTime.HasValue)
            {
                // Ensure history is attached locally for this play session
                if (item.History == null)
                {
                    item.History = _dataService.GetHistory(item.Id);
                }
            }

            ResetHudTimer();
        }

        public void OpenProgramme(Baird.Services.MediaItem programme)
        {
            var vm = new ProgrammeDetailViewModel(_dataService, programme);
            vm.PlayRequested += (s, item) =>
            {
                // TODO: this is generic?
                PlayItem(item);
                // Set current episode list for auto-play next episode
                // (after PlayItem, which clears it to handle Search/History plays correctly)
                // TODO: Don't use episode list, go straight to the datastore
                _currentEpisodeList = vm.ProgrammeChildren.ToList();

                // Set season context - programme.Id might be a season ID (showId|seasonNumber)
                // or just a show ID if there's only one season
                _currentSeasonId = programme.Id;

                // Extract base show ID (without season suffix)
                if (programme.Id.Contains("|"))
                {
                    var parts = programme.Id.Split('|');
                    _currentShowId = parts[0];
                }
                else
                {
                    _currentShowId = programme.Id;
                }

                Console.WriteLine($"[MainViewModel] Set episode list with {_currentEpisodeList.Count} episodes, showId={_currentShowId}, seasonId={_currentSeasonId}");
            };
            vm.BackRequested += (s, e) => GoBack();
            PushViewModel(vm);
        }

        public void CloseProgramme()
        {
            // Specifically pop if top is ProgrammeDetailViewModel?
            // Or generically pop?
            // Let's generically pop for now, assuming OpenProgramme pushed it last.
            if (CurrentPage is ProgrammeDetailViewModel)
            {
                PopViewModel();
            }
        }

        public void OpenMainMenu()
        {
            // History is now preloaded and maintained in memory, no need to refresh
            PushViewModel(this.MainMenu);
        }
    }
}
