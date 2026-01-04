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

        public OmniSearchViewModel OmniSearch { get; } = new();

        private ActiveMedia? _activeItem;
        public ActiveMedia? ActiveItem
        {
            get => _activeItem;
            set => this.RaiseAndSetIfChanged(ref _activeItem, value);
        }

        public MainViewModel()
        {
        }
    }
}
