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
using Avalonia.Threading;

namespace Baird.ViewModels
{
    public class ProviderSearchStatus : ReactiveObject
    {
        private string _name = "";
        public string Name
        {
            get => _name;
            set => this.RaiseAndSetIfChanged(ref _name, value);
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        private bool _isSuccess;
        public bool IsSuccess
        {
            get => _isSuccess;
            set => this.RaiseAndSetIfChanged(ref _isSuccess, value);
        }

        private bool _isError;
        public bool IsError
        {
            get => _isError;
            set => this.RaiseAndSetIfChanged(ref _isError, value);
        }

        private bool _isEmpty;
        public bool IsEmpty
        {
            get => _isEmpty;
            set => this.RaiseAndSetIfChanged(ref _isEmpty, value);
        }

        private int _resultCount;
        public int ResultCount
        {
            get => _resultCount;
            set => this.RaiseAndSetIfChanged(ref _resultCount, value);
        }

        // Shows "ProviderName (N)" when results were found, or just "ProviderName" when empty/idle.
        public string StatusLabel => ResultCount > 0 ? $"{Name} ({ResultCount})" : Name;

        public void SetLoading() { IsLoading = true; IsSuccess = false; IsError = false; IsEmpty = false; ResultCount = 0; this.RaisePropertyChanged(nameof(StatusBrush)); this.RaisePropertyChanged(nameof(StatusLabel)); }
        public void SetSuccess(int count) { IsLoading = false; IsSuccess = true; IsError = false; IsEmpty = false; ResultCount = count; this.RaisePropertyChanged(nameof(StatusBrush)); this.RaisePropertyChanged(nameof(StatusLabel)); }
        public void SetEmpty() { IsLoading = false; IsSuccess = false; IsError = false; IsEmpty = true; ResultCount = 0; this.RaisePropertyChanged(nameof(StatusBrush)); this.RaisePropertyChanged(nameof(StatusLabel)); }
        public void SetError() { IsLoading = false; IsSuccess = false; IsError = true; IsEmpty = false; ResultCount = 0; this.RaisePropertyChanged(nameof(StatusBrush)); this.RaisePropertyChanged(nameof(StatusLabel)); }
        public void SetIdle() { IsLoading = false; IsSuccess = false; IsError = false; IsEmpty = false; ResultCount = 0; this.RaisePropertyChanged(nameof(StatusBrush)); this.RaisePropertyChanged(nameof(StatusLabel)); }

        public Avalonia.Media.IBrush StatusBrush
        {
            get
            {
                if (IsError) return Avalonia.Media.Brushes.Red;
                if (IsEmpty) return Avalonia.Media.Brushes.Orange;
                if (IsSuccess) return Avalonia.Media.Brushes.Green;
                return Avalonia.Media.Brushes.Gray;
            }
        }
    }

    public class OmniSearchViewModel : ReactiveObject
    {
        public event EventHandler<MediaItemViewModel>? PlayRequested;
        public event EventHandler? BackRequested;
        public event EventHandler? SearchBoxFocusRequested;

        public ReactiveCommand<MediaItemViewModel, Unit> PlayCommand { get; }
        public ReactiveCommand<Unit, Unit> PlayFirstResultCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }
        public ReactiveCommand<Unit, Unit> BackIfEmptyCommand { get; }

        public void RequestPlay(MediaItemViewModel item)
        {
            PlayRequested?.Invoke(this, item);
        }

        public void RequestBack()
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        public void RequestSearchBoxFocus()
        {
            // Set flag so view can pick it up if not attached yet
            FocusSearchBoxOnLoad = true;
            SearchBoxFocusRequested?.Invoke(this, EventArgs.Empty);
        }

        private bool _focusSearchBoxOnLoad;
        public bool FocusSearchBoxOnLoad
        {
            get => _focusSearchBoxOnLoad;
            set => this.RaiseAndSetIfChanged(ref _focusSearchBoxOnLoad, value);
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

        public bool ShowSpinner => IsSearching && SearchResults.Count == 0;

        private bool _isAutoActivationPending;
        public bool IsAutoActivationPending
        {
            get => _isAutoActivationPending;
            set => this.RaiseAndSetIfChanged(ref _isAutoActivationPending, value);
        }

        private double _autoActivationProgress;
        public double AutoActivationProgress
        {
            get => _autoActivationProgress;
            set => this.RaiseAndSetIfChanged(ref _autoActivationProgress, value);
        }

        public ObservableCollection<MediaItemViewModel> SearchResults { get; } = new();
        public ObservableCollection<MediaRowViewModel> SearchResultRows { get; } = new();
        public ObservableCollection<string> SuggestedTerms { get; } = new();
        public ObservableCollection<ProviderSearchStatus> ProviderStatuses { get; } = new();

        private const int MaxInitialResults = 100;
        private List<MediaItemViewModel> _allResults = new();

        private bool _hasMoreResults;
        public bool HasMoreResults
        {
            get => _hasMoreResults;
            set => this.RaiseAndSetIfChanged(ref _hasMoreResults, value);
        }

        private void InitializeProviderStatuses()
        {
            if (ProviderStatuses.Any()) return;

            foreach (var provider in _dataService.Providers)
            {
                ProviderStatuses.Add(new ProviderSearchStatus { Name = provider.Name });
            }
        }

        // private readonly IEnumerable<IMediaProvider> _providers; // Removed
        private readonly IDataService _dataService;
        private readonly ISearchHistoryService _searchHistoryService;
        private readonly Func<List<MediaItemViewModel>> _getAllChannels;
        private DispatcherTimer? _autoActivationTimer;
        private DateTime _timerStartTime;

        public ReactiveCommand<string, Unit> SearchTermCommand { get; }
        public ReactiveCommand<MediaItemViewModel, Unit> AddToWatchlistCommand { get; }
        public ReactiveCommand<Unit, Unit> LoadMoreCommand { get; }

        public OmniSearchViewModel(IDataService dataService, ISearchHistoryService searchHistoryService, Func<List<MediaItemViewModel>> getAllChannels)
        {
            _dataService = dataService;
            _searchHistoryService = searchHistoryService;
            _getAllChannels = getAllChannels;

            PlayCommand = ReactiveCommand.CreateFromTask<MediaItemViewModel>(async item =>
            {
                // Record search term if we are currently searching and result is clicked
                if (!string.IsNullOrWhiteSpace(SearchText))
                {
                    await _searchHistoryService.AddSearchTermAsync(SearchText);
                    // Refresh suggestions logic can happen next time or now?
                    // Let's defer refresh to next open or explicit refresh
                }
                RequestPlay(item);
            });

            // PlayFirstResultCommand plays the first result when Enter is pressed on search textbox
            var canPlayFirst = this.WhenAnyValue(x => x.SearchResults.Count, count => count > 0);
            PlayFirstResultCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                if (SearchResults.FirstOrDefault() is MediaItemViewModel firstItem)
                {
                    if (!string.IsNullOrWhiteSpace(SearchText))
                    {
                        await _searchHistoryService.AddSearchTermAsync(SearchText);
                    }
                    RequestPlay(firstItem);
                }
            }, canPlayFirst);

            BackCommand = ReactiveCommand.Create(RequestBack);

            // BackIfEmptyCommand only executes when search text is empty
            var canBackIfEmpty = this.WhenAnyValue(x => x.SearchText, (string? text) => string.IsNullOrEmpty(text));
            BackIfEmptyCommand = ReactiveCommand.Create(RequestBack, canBackIfEmpty);

            SearchTermCommand = ReactiveCommand.CreateFromTask<string>(async term =>
            {
                SearchText = term;
                RequestSearchBoxFocus();
                await PerformSearch(term);
            });

            AddToWatchlistCommand = ReactiveCommand.CreateFromTask<MediaItemViewModel>(async item =>
            {
                await _dataService.AddToWatchlistAsync(item);
                Console.WriteLine($"[OmniSearch] Added {item.Name} to watchlist");
                // TODO: Visual feedback?
            });

            LoadMoreCommand = ReactiveCommand.Create(LoadMoreResults);

            var textChanges = this.WhenAnyValue(x => x.SearchText).Skip(1);

            // Immediate feedback: clear results when typing starts
            textChanges.Subscribe(_ =>
            {
                IsSearching = true;
                SearchResults.Clear();
                StopAutoActivationTimer();
            });

            // Notification for ShowSpinner
            this.WhenAnyValue(x => x.IsSearching).Subscribe(_ => this.RaisePropertyChanged(nameof(ShowSpinner)));
            SearchResults.CollectionChanged += (s, e) => this.RaisePropertyChanged(nameof(ShowSpinner));

            // Branch 1: Short numeric (<= 3 digits) -> Immediate
            textChanges
                .Where(q => !string.IsNullOrEmpty(q) && q.Length <= 3 && q.Length > 0 && q.All(char.IsDigit))
                .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                .Subscribe(async (q) => await PerformSearch(q));

            // Branch 2: Everything else -> Debounced
            textChanges
                .Where(q => string.IsNullOrEmpty(q) || q.Length > 3 || !q.All(char.IsDigit))
                .Throttle(TimeSpan.FromMilliseconds(300), RxApp.MainThreadScheduler)
                .Subscribe(async (q) => await PerformSearch(q));

            // Initial load of suggestions
            RefreshSuggestions();
            InitializeProviderStatuses();
        }

        public async void RefreshSuggestions()
        {
            try
            {
                var terms = await _searchHistoryService.GetSuggestedTermsAsync(5);
                SuggestedTerms.Clear();
                foreach (var term in terms)
                {
                    SuggestedTerms.Add(term);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OmniSearchViewModel] Error refreshing suggestions: {ex}");
            }
        }

        private CancellationTokenSource? _searchCts;

        private async Task PerformSearch(string? searchText)
        {
            var query = searchText ?? "";

            Console.WriteLine($"[OmniSearch] PerformSearch called with query='{query}'");

            // Cancel previous search
            _searchCts?.Cancel();
            var newCts = new CancellationTokenSource();
            _searchCts = newCts;
            var token = newCts.Token; // Capture token from local source

            IsSearching = true;
            SearchResults.Clear(); // Already on UI thread due to Throttle scheduler
            SearchResultRows.Clear();
            _allResults.Clear();
            HasMoreResults = false;

            // Initialize provider statuses if needed
            InitializeProviderStatuses();

            // Reset all to loading
            foreach (var status in ProviderStatuses)
            {
                status.SetLoading();
            }

            // For short numeric queries (1-3 digits), do in-memory channel search
            bool isShortNumericQuery = !string.IsNullOrEmpty(query) && query.Length <= 3 && query.All(char.IsDigit);

            if (isShortNumericQuery)
            {
                // In-memory channel number search
                var allChannels = _getAllChannels();
                var matchingChannels = allChannels
                    .Where(c => c.ChannelNumber != null &&
                               (c.ChannelNumber == query || c.ChannelNumber.StartsWith(query)))
                    .OrderBy(c => c.ChannelNumber?.Length)
                    .ThenBy(c => c.ChannelNumber)
                    .ToList();

                SearchResults.AddRange(matchingChannels);

                // Set providers to idle since we didn't use them
                foreach (var status in ProviderStatuses) status.SetIdle();

                IsSearching = false;

                // Start timer only if we found matching channels
                if (matchingChannels.Any())
                {
                    StartAutoActivationTimer();
                }

                // Clean up cancellation token
                newCts.Dispose();
                if (_searchCts == newCts)
                {
                    _searchCts = null;
                }

                return; // Skip provider search
            }

            var sorter = new SearchResultSorter();

            try
            {
                // Create tasks for each provider
                var tasks = _dataService.Providers.Select(async provider =>
                {
                    var status = ProviderStatuses.FirstOrDefault(p => p.Name == provider.Name);

                    try
                    {
                        var results = await provider.SearchAsync(query, token);
                        if (token.IsCancellationRequested) return;

                        var items = results.Select(data => new MediaItemViewModel(data)).ToList();

                        // Use UnifyAndHydrate to get cached instances with history/watchlist attached
                        var unifiedItems = _dataService.UnifyAndHydrate(items).ToList();
                        var count = unifiedItems.Count;

                        Console.WriteLine($"[OmniSearch] Provider '{provider.Name}' returned {count} result(s) for query '{query}'");

                        // Update UI atomically
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (unifiedItems.Any())
                            {
                                SearchResults.AddRange(unifiedItems);
                            }
                            if (status != null)
                            {
                                if (count > 0)
                                    status.SetSuccess(count);
                                else
                                    status.SetEmpty();
                            }
                        });
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine($"[OmniSearch] Provider '{provider.Name}' search cancelled for query '{query}'");
                        // Don't mark as error if canceled
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[OmniSearch] Provider {provider.Name} failed: {ex}");
                        if (status != null) Dispatcher.UIThread.Post(() => status.SetError());
                    }
                });

                await Task.WhenAll(tasks);

                // Final sort after all results are in
                if (SearchResults.Any())
                {
                    var allItems = SearchResults.ToList();
                    var sorted = sorter.Sort(allItems, query);
                    _allResults = sorted;

                    // Populate SearchResults with initial batch
                    var initialBatch = _allResults.Take(MaxInitialResults).ToList();
                    SearchResults.Clear();
                    SearchResults.AddRange(initialBatch);

                    // Update pagination state
                    HasMoreResults = _allResults.Count > MaxInitialResults;

                    // Populate row collection for virtualization
                    UpdateSearchResultRows();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[OmniSearchViewModel] Search error: {ex}");
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    IsSearching = false;
                }

                // Dispose the local CTS that we created for THIS search
                newCts.Dispose();

                // Only clear the shared field if it still points to our CTS (i.e. hasn't been replaced by a newer search)
                if (_searchCts == newCts)
                {
                    _searchCts = null;
                }
            }
        }

        private void StartAutoActivationTimer()
        {
            StopAutoActivationTimer();

            IsAutoActivationPending = true;
            AutoActivationProgress = 0;
            _timerStartTime = DateTime.Now;

            _autoActivationTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50) // Update every 50ms for smooth progress
            };

            _autoActivationTimer.Tick += OnAutoActivationTimerTick;
            _autoActivationTimer.Start();
        }

        private void StopAutoActivationTimer()
        {
            if (_autoActivationTimer != null)
            {
                _autoActivationTimer.Stop();
                _autoActivationTimer.Tick -= OnAutoActivationTimerTick;
                _autoActivationTimer = null;
            }

            IsAutoActivationPending = false;
            AutoActivationProgress = 0;
        }

        private void OnAutoActivationTimerTick(object? sender, EventArgs e)
        {
            const double timeoutSeconds = 3.0;
            var elapsed = (DateTime.Now - _timerStartTime).TotalSeconds;
            AutoActivationProgress = Math.Min(elapsed / timeoutSeconds, 1.0);

            if (AutoActivationProgress >= 1.0)
            {
                StopAutoActivationTimer();

                // Auto-activate the first result
                if (SearchResults.FirstOrDefault() is MediaItemViewModel firstItem)
                {
                    // Record search term for auto-activation?
                    // "Only record a search term as used when an item is actually actived (played or opened) from that search term."
                    // Yes, auto-activation counts as activation.
                    if (!string.IsNullOrWhiteSpace(SearchText))
                    {
                        _searchHistoryService.AddSearchTermAsync(SearchText);
                    }
                    RequestPlay(firstItem);
                }
            }
        }

        public void Clear()
        {
            SearchText = "";
            SearchResults.Clear();
            SearchResultRows.Clear();
            _allResults.Clear();
            HasMoreResults = false;
            IsSearching = false;
            StopAutoActivationTimer();
            // Refresh suggestions on clear/open
            RefreshSuggestions();
        }

        private void LoadMoreResults()
        {
            if (!HasMoreResults) return;

            var currentCount = SearchResults.Count;
            var currentRowCount = SearchResultRows.Count;
            var nextBatch = _allResults.Skip(currentCount).Take(MaxInitialResults).ToList();

            SearchResults.AddRange(nextBatch);
            HasMoreResults = SearchResults.Count < _allResults.Count;

            // Check if we need to update the last partial row or just add new rows
            var itemsInLastRow = currentCount % 6;

            if (itemsInLastRow > 0 && nextBatch.Any())
            {
                // Last row was partial, need to rebuild it with additional items
                var itemsToCompleteRow = Math.Min(6 - itemsInLastRow, nextBatch.Count);
                var lastRowStartIndex = currentCount - itemsInLastRow;
                var lastRowItems = SearchResults.Skip(lastRowStartIndex).Take(6).ToList();

                // Update the last row
                SearchResultRows[currentRowCount - 1] = MediaRowViewModel.CreateRows(lastRowItems)[0];

                // Add any remaining items as new rows
                if (nextBatch.Count > itemsToCompleteRow)
                {
                    var remainingItems = nextBatch.Skip(itemsToCompleteRow).ToList();
                    var newRows = MediaRowViewModel.CreateRows(remainingItems);
                    foreach (var row in newRows)
                    {
                        SearchResultRows.Add(row);
                    }
                }
            }
            else
            {
                // Last row was complete, just add new rows
                var newRows = MediaRowViewModel.CreateRows(nextBatch);
                foreach (var row in newRows)
                {
                    SearchResultRows.Add(row);
                }
            }
        }

        private void UpdateSearchResultRows()
        {
            // Only update from scratch when necessary (e.g., after search)
            // For incremental updates, use LoadMoreResults pattern
            var rows = MediaRowViewModel.CreateRows(SearchResults);
            SearchResultRows.Clear();
            SearchResultRows.AddRange(rows);
        }
    }
}
