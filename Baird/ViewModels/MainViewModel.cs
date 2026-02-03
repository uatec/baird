using ReactiveUI;

namespace Baird.ViewModels
{
    public class MainViewModel : ReactiveObject
    {
        private bool _isSearchActive;
        public bool IsSearchActive
        {
            get => _isSearchActive;
            set => this.RaiseAndSetIfChanged(ref _isSearchActive, value);
        }


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
            ProgrammeChildren = new System.Collections.ObjectModel.ObservableCollection<Baird.Services.MediaItem>();
            
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            AppVersion = version != null ? $"v{version.Major}.{version.Minor}.{version.Build}" : "v0.0.0";
        }

        private readonly System.Collections.Generic.IEnumerable<Baird.Services.IMediaProvider> _providers;

        private Baird.Services.MediaItem? _selectedProgramme;
        public Baird.Services.MediaItem? SelectedProgramme
        {
            get => _selectedProgramme;
            set => this.RaiseAndSetIfChanged(ref _selectedProgramme, value);
        }

        private bool _isProgrammeDetailVisible;
        public bool IsProgrammeDetailVisible
        {
            get => _isProgrammeDetailVisible;
            set => this.RaiseAndSetIfChanged(ref _isProgrammeDetailVisible, value);
        }

        public System.Collections.ObjectModel.ObservableCollection<Baird.Services.MediaItem> ProgrammeChildren { get; }

        public async System.Threading.Tasks.Task OpenProgramme(Baird.Services.MediaItem programme)
        {
            SelectedProgramme = programme;
            ProgrammeChildren.Clear();
            IsProgrammeDetailVisible = true;
            
            // Find the provider that owns this item (simplified by Source string or try all)
            // For now, BbcIPlayerService is the only one returning Brands.
            // Ideally we'd map "BBC iPlayer" -> BbcIPlayerService.
            
            // Just try all providers, fast one wins.
            var tasks = new System.Collections.Generic.List<System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<Baird.Services.MediaItem>>>();
            foreach(var p in _providers)
            {
               tasks.Add(p.GetChildrenAsync(programme.Id));
            }

            try 
            {
                var results = await System.Threading.Tasks.Task.WhenAll(tasks);
                foreach(var list in results)
                {
                    foreach(var item in list)
                    {
                        ProgrammeChildren.Add(item);
                    }
                }
            }
            catch(System.Exception ex)
            {
                System.Console.WriteLine($"Error fetching children: {ex.Message}");
            }
        }

        public void CloseProgramme()
        {
            IsProgrammeDetailVisible = false;
            SelectedProgramme = null;
            ProgrammeChildren.Clear();
        }
    }
}
