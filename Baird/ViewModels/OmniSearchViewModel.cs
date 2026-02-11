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
    public class OmniSearchViewModel : ReactiveObject
    {
        public event EventHandler<MediaItem>? PlayRequested;
        public event EventHandler? BackRequested;

        public ReactiveCommand<MediaItem, Unit> PlayCommand { get; }
        public ReactiveCommand<Unit, Unit> PlayFirstResultCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }
        public ReactiveCommand<Unit, Unit> BackIfEmptyCommand { get; }

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

        public ObservableCollection<MediaItem> SearchResults { get; } = new();
        private readonly IEnumerable<IMediaProvider> _providers;
        private readonly Func<List<MediaItem>> _getAllChannels;
        private DispatcherTimer? _autoActivationTimer;
        private DateTime _timerStartTime;

        public OmniSearchViewModel(IEnumerable<IMediaProvider> providers, Func<List<MediaItem>> getAllChannels)
        {
            _providers = providers;
            _getAllChannels = getAllChannels;

            PlayCommand = ReactiveCommand.Create<MediaItem>(RequestPlay);

            // PlayFirstResultCommand plays the first result when Enter is pressed on search textbox
            var canPlayFirst = this.WhenAnyValue(x => x.SearchResults.Count, count => count > 0);
            PlayFirstResultCommand = ReactiveCommand.Create(() =>
            {
                if (SearchResults.FirstOrDefault() is MediaItem firstItem)
                {
                    RequestPlay(firstItem);
                }
            }, canPlayFirst);

            BackCommand = ReactiveCommand.Create(RequestBack);

            // BackIfEmptyCommand only executes when search text is empty
            var canBackIfEmpty = this.WhenAnyValue(x => x.SearchText, (string? text) => string.IsNullOrEmpty(text));
            BackIfEmptyCommand = ReactiveCommand.Create(RequestBack, canBackIfEmpty);

            var textChanges = this.WhenAnyValue(x => x.SearchText).Skip(1);

            // Immediate feedback: clear results when typing starts
            textChanges.Subscribe(_ =>
            {
                IsSearching = true;
                SearchResults.Clear();
                StopAutoActivationTimer();
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
            var newCts = new CancellationTokenSource();
            _searchCts = newCts;
            var token = newCts.Token; // Capture token from local source

            IsSearching = true;
            SearchResults.Clear(); // Already on UI thread due to Throttle scheduler

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
                if (SearchResults.FirstOrDefault() is MediaItem firstItem)
                {
                    RequestPlay(firstItem);
                }
            }
        }

        public void Clear()
        {
            SearchText = "";
            SearchResults.Clear();
            IsSearching = false;
            StopAutoActivationTimer();
        }

        public async Task ClearAndSearch()
        {
            SearchText = "";
            await PerformSearch("");
        }
    }
}
