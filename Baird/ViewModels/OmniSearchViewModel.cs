using System.Collections.ObjectModel;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using Baird.Services;
using System.Linq;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using System.Threading;

namespace Baird.ViewModels
{
    public class OmniSearchViewModel : ReactiveObject
    {
        public event EventHandler<MediaItem>? PlayRequested;
        public event EventHandler? BackRequested;

        public ReactiveCommand<MediaItem, Unit> PlayCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        public void RequestPlay(MediaItem item)
        {
            PlayRequested?.Invoke(this, item);
        }

        public void RequestBack()
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }
        
        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set => this.RaiseAndSetIfChanged(ref _searchText, value);
        }

        private bool _isSearching;
        public bool IsSearching
        {
            get => _isSearching;
            set => this.RaiseAndSetIfChanged(ref _isSearching, value);
        }
        
        private MediaItem? _selectedItem;
        public MediaItem? SelectedItem
        {
            get => _selectedItem;
            set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
        }

        public ObservableCollection<MediaItem> SearchResults { get; } = new();
        private readonly IEnumerable<IMediaProvider> _providers;

        public OmniSearchViewModel(IEnumerable<IMediaProvider> providers)
        {
            _providers = providers;

            PlayCommand = ReactiveCommand.Create<MediaItem>(RequestPlay);
            BackCommand = ReactiveCommand.Create(RequestBack);
            
            var textChanges = this.WhenAnyValue(x => x.SearchText).Skip(1);

            // Immediate feedback: clear results when typing starts
            textChanges.Subscribe(_ => 
            {
                IsSearching = true;
                SelectedItem = null;
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
                                
                                // Auto-select first item if nothing is selected yet
                                if (SelectedItem == null && SearchResults.Count > 0)
                                {
                                    SelectedItem = SearchResults[0];
                                }
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
        
        public void Clear()
        {
            SearchText = "";
            SearchResults.Clear();
            SelectedItem = null;
            IsSearching = false;
        }

        public async Task ClearAndSearch()
        {
            SearchText = "";
            await PerformSearch("");
        }
    }
}
