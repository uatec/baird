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
using DynamicData;

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

        private bool _isSearchFieldFocused;
        public bool IsSearchFieldFocused
        {
            get => _isSearchFieldFocused;
            set => this.RaiseAndSetIfChanged(ref _isSearchFieldFocused, value);
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

            var canPlay = this.WhenAnyValue(x => x.SelectedItem, (MediaItem? item) => item != null);
            PlayCommand = ReactiveCommand.Create<MediaItem>(RequestPlay, canPlay);
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
                .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
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
            
            var accumulatedResults = new List<MediaItem>();
            var sorter = new SearchResultSorter();

            // Create tasks for each provider
            var tasks = _providers.Select(async provider => 
            {
                try 
                {
                    var results = await provider.SearchAsync(query, token);
                    
                    if (token.IsCancellationRequested) return;

                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                    {
                        if (token.IsCancellationRequested) return;

                        var newItems = results.ToList();
                        if (newItems.Any())
                        {
                            accumulatedResults.AddRange(newItems);
                            
                            if (IsSearchFieldFocused)
                            {
                                // Re-sort everything
                                var sorted = sorter.Sort(accumulatedResults, query);
                                SearchResults.Clear();
                                SearchResults.AddRange(sorted);
                                
                                // Auto-select first if nothing selected or selection lost
                                if (SelectedItem == null)
                                {
                                    SelectedItem = SearchResults.FirstOrDefault();
                                }
                            }
                            else
                            {
                                // Append only
                                // We might want to sort the *new* batch? 
                                // Or just append them raw?
                                // "if the focus has moved to the list items, please append only so items don't move under the user"
                                // Appending raw preserves "accumulated" order, which is "arrival time".
                                // But if a provider returns a mix of "High Priority" and "Low Priority", we should probably sort the *batch*?
                                // Let's sort the batch using the same logic, but just for this batch.
                                var sortedBatch = sorter.Sort(newItems, query);
                                SearchResults.AddRange(sortedBatch);
                            }
                        }
                    });
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"Search provider error: {ex}");
                }
            }).ToList();

            try 
            {
                await Task.WhenAll(tasks);
            }
            catch (Exception) 
            { 
               // Ignore aggregate exceptions from cancellation 
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    IsSearching = false;
                }
                _searchCts.Dispose();
                _searchCts = null;
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
