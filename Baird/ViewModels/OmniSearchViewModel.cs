using System.Collections.ObjectModel;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using Baird.Services;
using System.Linq;

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

        private bool _isSearching;
        public bool IsSearching
        {
            get => _isSearching;
            set => this.RaiseAndSetIfChanged(ref _isSearching, value);
        }

        public ObservableCollection<MediaItem> SearchResults { get; } = new();
        private readonly IEnumerable<IMediaProvider> _providers;

        public OmniSearchViewModel(IEnumerable<IMediaProvider> providers)
        {
            _providers = providers;
            
            var textChanges = this.WhenAnyValue(x => x.SearchText).Skip(1);

            // Immediate feedback: clear results when typing starts
            textChanges.Subscribe(_ => 
            {
                IsSearching = true;
                SearchResults.Clear();
            });

            // Branch 1: Short numeric (<= 3 digits) -> Immediate
            textChanges
                .Where(q => !string.IsNullOrEmpty(q) && q.Length <= 3 && q.All(char.IsDigit))
                .Subscribe(async (q) => await PerformSearch(q));

            // Branch 2: Everything else -> Debounced
            textChanges
                .Where(q => string.IsNullOrEmpty(q) || q.Length > 3 || !q.All(char.IsDigit))
                .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                .Subscribe(async (q) => await PerformSearch(q));
        }

        private CancellationTokenSource? _searchCts;

        private async Task PerformSearch(string? searchText)
        {
            var query = searchText ?? "";
            
            // Cancel previous search
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var token = _searchCts.Token;

            IsSearching = true;
            SearchResults.Clear(); // Already on UI thread due to Throttle scheduler

            var tasks = _providers.Select(async provider =>
            {
                try 
                {
                    var providerResults = await provider.SearchAsync(query);
                    if (token.IsCancellationRequested) return;

                    if (providerResults != null)
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                        {
                            if (!token.IsCancellationRequested)
                            {
                                foreach (var item in providerResults) SearchResults.Add(item);
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Search error in provider {provider.GetType().Name}: {ex.Message}");
                }
            });

            try
            {
                await Task.WhenAll(tasks);
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    IsSearching = false;
                }
            }
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
            IsSearching = false;
        }

        public async Task ClearAndSearch()
        {
            SearchText = "";
            await PerformSearch("");
        }
    }
}
