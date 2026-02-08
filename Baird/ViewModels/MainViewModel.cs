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

        private ActiveMedia? _activeItem;
        public ActiveMedia? ActiveItem
        {
            get => _activeItem;
            set => this.RaiseAndSetIfChanged(ref _activeItem, value);
        }

        public string AppVersion { get; }

        public MainViewModel(System.Collections.Generic.IEnumerable<Baird.Services.IMediaProvider> providers)
        {
            _providers = providers;
            OmniSearch = new OmniSearchViewModel(providers);
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
                // IsPaused = true; // Optional: pause background if entering detail view?
                                   // But maybe not if we want audio to continue?
                                   // Existing logic did set IsPaused=true.
                IsPaused = true;
                OpenProgramme(item);
                return;
            }

            if (!string.IsNullOrEmpty(item.StreamUrl))
            {
                ActivateChannel(item);
                
                // Clear navigation stack to return to video
                NavigationHistory.Clear();
                CurrentPage = null;
                
                // Also clear search if it was active (it might be deep in stack or top)
                OmniSearch.Clear();
            }
        }

        public void GoBack()
        {
           PopViewModel();
           // If we popped to nothing (Video), ensure specific focus or state?
           if (CurrentPage == null)
           {
               // We are back at video
           }
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
                if(AllChannels.Count > 0) ActivateChannel(AllChannels[0]);
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
                if(AllChannels.Count > 0) ActivateChannel(AllChannels[0]);
                return;
            }

            var prevIndex = (currentIndex - 1 + AllChannels.Count) % AllChannels.Count;
            ActivateChannel(AllChannels[prevIndex]);
        }

        // Helper to activate a channel (similar to PlayItem in View, but VM based)
        // Note: Actual playback starts because ActiveItem is bound in View.
        private void ActivateChannel(Baird.Services.MediaItem item)
        {
             ActiveItem = new ActiveMedia 
             {
                 Id = item.Id,
                 Name = item.Name,
                 Details = item.Details,
                 StreamUrl = item.StreamUrl,
                 IsLive = item.IsLive,
                 ChannelNumber = item.ChannelNumber
             };
             
             ResetHudTimer();
        }

        public void OpenProgramme(Baird.Services.MediaItem programme)
        {
            var vm = new ProgrammeDetailViewModel(_providers, programme);
            vm.PlayRequested += (s, item) => PlayItem(item);
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
    }
}
