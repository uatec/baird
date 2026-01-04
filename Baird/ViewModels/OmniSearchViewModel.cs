using System.Collections.ObjectModel;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using Baird.Services;

namespace Baird.ViewModels
{
    public class OmniSearchViewModel : ReactiveObject
    {
        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set => this.RaiseAndSetIfChanged(ref _searchText, value);
        }

        private bool _isKeyboardVisible;
        public bool IsKeyboardVisible
        {
            get => _isKeyboardVisible;
            set => this.RaiseAndSetIfChanged(ref _isKeyboardVisible, value);
        }

        public ObservableCollection<MediaItem> SearchResults { get; } = new();
        private readonly IEnumerable<IMediaProvider> _providers;

        public OmniSearchViewModel(IEnumerable<IMediaProvider> providers)
        {
            _providers = providers;
            this.WhenAnyValue(x => x.SearchText)
                .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                .Subscribe(async (q) => await PerformSearch(q));
        }

        private async Task PerformSearch(string? searchText)
        {
            var query = searchText ?? "";
            
            // In a real app we might want to cancel previous requests.
            // For now, simpler: clear and fetch.
            
            var results = new List<MediaItem>();
            foreach (var provider in _providers)
            {
                try 
                {
                    var providerResults = await provider.SearchAsync(query);
                    if (providerResults != null)
                    {
                        results.AddRange(providerResults);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Search error in provider {provider.GetType().Name}: {ex.Message}");
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() => 
            {
                SearchResults.Clear();
                foreach (var item in results) SearchResults.Add(item);
            });
        }

        public void AppendDigit(string digit)
        {
            SearchText += digit;
        }

        public void Backspace()
        {
            if (!string.IsNullOrEmpty(SearchText))
            {
                SearchText = SearchText.Substring(0, SearchText.Length - 1);
            }
        }

        public void Clear()
        {
            SearchText = "";
            SearchResults.Clear();
        }

        public async Task ClearAndSearch()
        {
            SearchText = "";
            await PerformSearch("");
        }
    }
}
