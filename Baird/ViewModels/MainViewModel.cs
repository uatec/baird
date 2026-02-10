using ReactiveUI;
using System.Runtime.InteropServices;
using Baird.Services;

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

        private MediaItem? _activeItem;
        public MediaItem? ActiveItem
        {
            get => _activeItem;
            set => this.RaiseAndSetIfChanged(ref _activeItem, value);
        }

        public string AppVersion { get; }
        public IHistoryService HistoryService { get; } // Exposed for now, or just internal use

        // Track current episode list for auto-play next episode
        private System.Collections.Generic.List<MediaItem>? _currentEpisodeList;

        public MainViewModel(System.Collections.Generic.IEnumerable<Baird.Services.IMediaProvider> providers, IHistoryService historyService)
        {
            _providers = providers;
            HistoryService = historyService;
            OmniSearch = new OmniSearchViewModel(providers, () => AllChannels);
            History = new HistoryViewModel(historyService);

            History.PlayRequested += (s, item) => PlayItem(item);
            History.BackRequested += (s, e) => GoBack();

            IsVideoHudVisible = true;

            IsSubtitlesEnabled = NativeUtils.GetCapsLockState();

            // ProgrammeChildren = new System.Collections.ObjectModel.ObservableCollection<Baird.Services.MediaItem>();

            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            AppVersion = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v0.0.0";

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

            OmniSearch.PlayRequested += (s, item) => PlayItem(item);
            OmniSearch.BackRequested += (s, e) => GoBack();
        }

        private Avalonia.Threading.DispatcherTimer _hudTimer;
        private readonly System.Collections.Generic.IEnumerable<Baird.Services.IMediaProvider> _providers;

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
                // Clear episode list when opening a programme (not playing an episode)
                _currentEpisodeList = null;
                OpenProgramme(item);
                return;
            }

            // Clear episode list for videos
            // (will be set after by OpenProgramme.PlayRequested if playing from programme details)
            _currentEpisodeList = null;

            if (!string.IsNullOrEmpty(item.StreamUrl))
            {
                // Check for resume progress
                var history = HistoryService.GetProgress(item.Id);
                TimeSpan? resumeTime = null;

                if (history != null && !history.IsFinished && !item.IsLive)
                {
                    // Resume logic
                    resumeTime = history.LastPosition;
                    Console.WriteLine($"Resuming {item.Name} at {resumeTime}");
                }

                ActivateChannel(item, resumeTime);

                // Set CurrentPage to null to show video player, but preserve navigation history
                // so user can navigate back to their previous page
                CurrentPage = null;
            }
        }

        public void GoBack()
        {
            // If we're on the video player (CurrentPage == null) and there's history,
            // restore the previous page
            if (CurrentPage == null && NavigationHistory.Count > 0)
            {
                CurrentPage = NavigationHistory.Peek();
                return;
            }

            // Otherwise, pop the current page to go back
            PopViewModel();
        }

        public Stack<ReactiveObject> NavigationHistory { get; } = new();

        private ReactiveObject? _currentPage;
        public ReactiveObject? CurrentPage
        {
            get => _currentPage;
            private set => this.RaiseAndSetIfChanged(ref _currentPage, value);
        }

        public void PushViewModel(ReactiveObject viewModel)
        {
            NavigationHistory.Push(viewModel);
            CurrentPage = viewModel;
        }

        public void PopViewModel()
        {
            if (NavigationHistory.Count > 0)
            {
                NavigationHistory.Pop();
                CurrentPage = NavigationHistory.Count > 0 ? NavigationHistory.Peek() : null;
            }
            else
            {
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

        public void PlayNextEpisodeOrGoBack()
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
                Console.WriteLine($"[MainViewModel] Auto-playing next episode: {nextEpisode.Name}");
                PlayItem(nextEpisode);
            }
            else
            {
                Console.WriteLine("[MainViewModel] No next episode, navigating back");
                PopViewModel();
            }
        }

        public System.Collections.Generic.List<Baird.Services.MediaItem> AllChannels { get; private set; } = new();

        public async System.Threading.Tasks.Task RefreshChannels()
        {
            var tasks = new System.Collections.Generic.List<System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<Baird.Services.MediaItem>>>();
            foreach (var provider in _providers)
            {
                tasks.Add(provider.GetListingAsync());
            }

            try
            {
                var results = await System.Threading.Tasks.Task.WhenAll(tasks);
                var allItems = new System.Collections.Generic.List<Baird.Services.MediaItem>();
                foreach (var list in results)
                {
                    allItems.AddRange(list);
                }

                AllChannels = allItems
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
                System.Console.WriteLine($"Error refreshing channels: {ex.Message}");
            }
        }

        public void SelectNextChannel()
        {
            Console.WriteLine($"SelectNextChannel: ActiveItem={ActiveItem?.Name}, AllChannels.Count={AllChannels.Count}");
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
            Console.WriteLine($"SelectPreviousChannel: ActiveItem={ActiveItem?.Name}, AllChannels.Count={AllChannels.Count}");
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
            // ActiveItem = new ActiveMedia
            // {
            //     Id = item.Id,
            //     Name = item.Name,
            //     Details = item.Details,
            //     ImageUrl = item.ImageUrl,
            //     Source = item.Source,
            //     Type = item.Type,
            //     Synopsis = item.Synopsis,
            //     Subtitle = item.Subtitle,
            //     StreamUrl = item.StreamUrl,
            //     IsLive = item.IsLive,
            //     ChannelNumber = item.ChannelNumber,
            //     ResumeTime = resumeTime
            // };

            ResetHudTimer();
        }

        public void OpenProgramme(Baird.Services.MediaItem programme)
        {
            var vm = new ProgrammeDetailViewModel(_providers, programme);
            vm.PlayRequested += (s, item) =>
            {
                PlayItem(item);
                // Set current episode list for auto-play next episode
                // (after PlayItem, which clears it to handle Search/History plays correctly)
                _currentEpisodeList = vm.ProgrammeChildren.ToList();
                Console.WriteLine($"[MainViewModel] Set episode list with {_currentEpisodeList.Count} episodes");
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

        public async void OpenHistory()
        {
            // Refresh history before showing
            await History.RefreshAsync();
            PushViewModel(History);
        }
    }
}
