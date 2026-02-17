using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using Baird.Services;
using DynamicData;

namespace Baird.ViewModels
{
    public class SeerrchResultViewModel : ReactiveObject
    {
        private readonly JellyseerrSearchResult _result;

        public SeerrchResultViewModel(JellyseerrSearchResult result)
        {
            _result = result;
        }

        public int Id => _result.Id;
        public string MediaType => _result.MediaType;
        public string Title => _result.Title;
        public string? PosterPath => _result.PosterPath;
        public string? Overview => _result.Overview;
        public string? ReleaseDate => _result.ReleaseDate;
        public double VoteAverage => _result.VoteAverage;
        public bool IsAvailable => _result.IsAvailable;

        // For display in tile
        public string FullPosterUrl
        {
            get
            {
                if (string.IsNullOrEmpty(PosterPath))
                    return "";
                return $"https://image.tmdb.org/t/p/w500{PosterPath}";
            }
        }

        public string StatusText
        {
            get
            {
                return _result.MediaInfoStatus switch
                {
                    5 => "Available",
                    4 => "Partial",
                    3 => "Processing",
                    2 => "Pending",
                    _ => ""
                };
            }
        }

        public string BadgeText => MediaType == "movie" ? "Movie" : "TV";
    }

    public class SeerrchViewModel : ReactiveObject
    {
        public event EventHandler? BackRequested;
        public event EventHandler? SearchBoxFocusRequested;

        private readonly IJellyseerrService _jellyseerrService;
        private CancellationTokenSource? _searchCts;

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

        private string _statusMessage = "";
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        private bool _showStatus;
        public bool ShowStatus
        {
            get => _showStatus;
            set => this.RaiseAndSetIfChanged(ref _showStatus, value);
        }

        public bool ShowSpinner => IsSearching && SearchResults.Count == 0;

        public ObservableCollection<SeerrchResultViewModel> SearchResults { get; } = new();
        public ObservableCollection<SeerrchRowViewModel> SearchResultRows { get; } = new();

        public ReactiveCommand<SeerrchResultViewModel, Unit> RequestItemCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        public SeerrchViewModel(IJellyseerrService jellyseerrService)
        {
            _jellyseerrService = jellyseerrService;

            RequestItemCommand = ReactiveCommand.CreateFromTask<SeerrchResultViewModel>(async item =>
            {
                await RequestItem(item);
            });

            BackCommand = ReactiveCommand.Create(() =>
            {
                BackRequested?.Invoke(this, EventArgs.Empty);
            });

            // Reactive search: debounce user input
            var textChanges = this.WhenAnyValue(x => x.SearchText)
                .Skip(1) // Skip initial value
                .Throttle(TimeSpan.FromMilliseconds(400), RxApp.MainThreadScheduler);

            textChanges.Subscribe(async query =>
            {
                await PerformSearch(query);
            });

            // Track searching state for spinner
            this.WhenAnyValue(x => x.IsSearching).Subscribe(_ => this.RaisePropertyChanged(nameof(ShowSpinner)));
            SearchResults.CollectionChanged += (s, e) => this.RaisePropertyChanged(nameof(ShowSpinner));
        }

        public void RequestSearchBoxFocus()
        {
            SearchBoxFocusRequested?.Invoke(this, EventArgs.Empty);
        }

        private async Task PerformSearch(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                SearchResults.Clear();
                SearchResultRows.Clear();
                IsSearching = false;
                ShowStatus = false;
                return;
            }

            // Cancel previous search
            _searchCts?.Cancel();
            var newCts = new CancellationTokenSource();
            _searchCts = newCts;
            var token = newCts.Token;

            IsSearching = true;
            SearchResults.Clear();
            ShowStatus = false;

            try
            {
                var results = await _jellyseerrService.SearchAsync(query, 1, token);

                if (token.IsCancellationRequested) return;

                var viewModels = results.Select(r => new SeerrchResultViewModel(r)).ToList();

                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested) return;
                    
                    SearchResults.Clear();
                    foreach (var vm in viewModels)
                    {
                        SearchResults.Add(vm);
                    }
                    
                    // Update row collection for virtualization
                    UpdateSearchResultRows();
                    
                    IsSearching = false;
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SeerrchViewModel] Search error: {ex.Message}");
                IsSearching = false;
                ShowStatus = true;
                StatusMessage = $"Search failed: {ex.Message}";
            }
            finally
            {
                _searchCts?.Dispose();
                if (_searchCts == newCts)
                {
                    _searchCts = null;
                }
            }
        }

        private async Task RequestItem(SeerrchResultViewModel item)
        {
            ShowStatus = true;
            StatusMessage = $"Requesting {item.Title}...";

            try
            {
                var response = await _jellyseerrService.CreateRequestAsync(item.Id, item.MediaType);

                if (response.Success)
                {
                    StatusMessage = $"✓ Successfully requested {item.Title}";
                    Console.WriteLine($"[SeerrchViewModel] Request successful: {item.Title} (ID: {response.RequestId})");
                    
                    // Auto-hide status after 3 seconds
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        Dispatcher.UIThread.Post(() => ShowStatus = false);
                    });
                }
                else
                {
                    StatusMessage = $"✗ Failed: {response.Message}";
                    Console.WriteLine($"[SeerrchViewModel] Request failed: {response.Message}");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"✗ Error: {ex.Message}";
                Console.WriteLine($"[SeerrchViewModel] Request error: {ex.Message}");
            }
        }

        private void UpdateSearchResultRows()
        {
            var rows = SeerrchRowViewModel.CreateRows(SearchResults);
            
            // Incrementally update rows to avoid disruption
            // Remove excess rows if list shrunk
            while (SearchResultRows.Count > rows.Length)
            {
                SearchResultRows.RemoveAt(SearchResultRows.Count - 1);
            }
            
            // Update existing rows and add new ones
            for (int i = 0; i < rows.Length; i++)
            {
                if (i < SearchResultRows.Count)
                {
                    // Replace existing row if items changed
                    if (!AreSameRowItems(SearchResultRows[i], rows[i]))
                    {
                        SearchResultRows[i] = rows[i];
                    }
                }
                else
                {
                    // Add new row
                    SearchResultRows.Add(rows[i]);
                }
            }
        }

        private bool AreSameRowItems(SeerrchRowViewModel row1, SeerrchRowViewModel row2)
        {
            return row1.Item1?.Id == row2.Item1?.Id &&
                   row1.Item2?.Id == row2.Item2?.Id &&
                   row1.Item3?.Id == row2.Item3?.Id &&
                   row1.Item4?.Id == row2.Item4?.Id &&
                   row1.Item5?.Id == row2.Item5?.Id &&
                   row1.Item6?.Id == row2.Item6?.Id;
        }
    }
}
