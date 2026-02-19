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

namespace Baird.ViewModels
{
    public class RequestItemViewModel : ReactiveObject
    {
        private readonly JellyseerrRequest _request;

        public RequestItemViewModel(JellyseerrRequest request)
        {
            _request = request;
        }

        public int Id => _request.Id;
        public string Title => _request.Title;
        public string MediaType => _request.MediaType;
        public int TmdbId => _request.TmdbId;
        public bool IsAvailable => _request.IsAvailable;
        public DateTime CreatedAt => DateTime.TryParse(_request.CreatedAt, out var date) ? date : DateTime.MinValue;
        public DateTime UpdatedAt => DateTime.TryParse(_request.UpdatedAt, out var date) ? date : DateTime.MinValue;

        public string FullPosterUrl
        {
            get
            {
                if (string.IsNullOrEmpty(_request.PosterPath))
                    return "";
                return $"https://image.tmdb.org/t/p/w500{_request.PosterPath}";
            }
        }

        public string StatusText
        {
            get
            {
                return _request.Status switch
                {
                    1 => "Pending",
                    2 => "Approved",
                    3 => "Declined",
                    _ => _request.MediaInfoStatus switch
                    {
                        5 => "Available",
                        4 => "Partial",
                        3 => "Processing",
                        2 => "Pending",
                        _ => "Unknown"
                    }
                };
            }
        }

        public string StatusColor
        {
            get
            {
                if (_request.Status == 3) return "#CC0000"; // Declined - Red
                if (_request.MediaInfoStatus == 5 || _request.MediaInfoStatus == 4) return "#00AA00"; // Available - Green
                if (_request.MediaInfoStatus == 3) return "#0088DD"; // Processing - Blue
                return "#CCAA00"; // Pending/Approved - Yellow
            }
        }

        public double Progress
        {
            get
            {
                // Calculate progress based on media status
                return _request.MediaInfoStatus switch
                {
                    5 => 100.0, // Available
                    4 => 90.0,  // Partially Available
                    3 => 60.0,  // Processing
                    2 => 30.0,  // Pending
                    _ => _request.Status switch
                    {
                        2 => 25.0,  // Approved
                        1 => 10.0,  // Pending approval
                        _ => 0.0
                    }
                };
            }
        }

        public string FormattedDate
        {
            get
            {
                var date = UpdatedAt != DateTime.MinValue ? UpdatedAt : CreatedAt;
                var timeAgo = DateTime.UtcNow - date;

                if (timeAgo.TotalHours < 1)
                    return $"{(int)timeAgo.TotalMinutes}m ago";
                if (timeAgo.TotalDays < 1)
                    return $"{(int)timeAgo.TotalHours}h ago";
                if (timeAgo.TotalDays < 7)
                    return $"{(int)timeAgo.TotalDays}d ago";

                return date.ToString("MMM dd");
            }
        }

        public string BadgeText => MediaType == "movie" ? "Movie" : "TV";
    }

    public class RequestsViewModel : ReactiveObject
    {
        public event EventHandler<MediaItemViewModel>? PlayRequested;

        private readonly IJellyseerrService _jellyseerrService;
        private readonly IDataService _dataService;
        private DispatcherTimer? _refreshTimer;

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => this.RaiseAndSetIfChanged(ref _isLoading, value);
        }

        private string _statusMessage = "";
        public string StatusMessage
        {
            get => _statusMessage;
            set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
        }

        public ObservableCollection<RequestItemViewModel> Requests { get; } = new();

        public ReactiveCommand<RequestItemViewModel, Unit> PlayCompletedCommand { get; }
        public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

        public RequestsViewModel(IJellyseerrService jellyseerrService, IDataService dataService)
        {
            _jellyseerrService = jellyseerrService;
            _dataService = dataService;

            // Command to play completed items
            PlayCompletedCommand = ReactiveCommand.CreateFromTask<RequestItemViewModel>(async item =>
            {
                if (item.IsAvailable)
                {
                    await PlayCompletedItem(item);
                }
            });

            RefreshCommand = ReactiveCommand.CreateFromTask(async () =>
            {
                await LoadRequests();
            });

            // Initial load (off UI context so continuations don't contend for UI thread)
            _ = Task.Run(() => LoadRequests());

            // Setup auto-refresh timer (every 30 seconds)
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _refreshTimer.Tick += async (s, e) => await LoadRequests();
            _refreshTimer.Start();
        }

        private async Task LoadRequests()
        {
            // Only show loading indicator if we have no data yet (first load)
            bool isFirstLoad = !Requests.Any();
            if (isFirstLoad)
            {
                IsLoading = true;
                StatusMessage = "Loading requests...";
            }

            try
            {
                var requests = await _jellyseerrService.GetRequestsAsync();
                var viewModels = requests
                    .Select(r => new RequestItemViewModel(r))
                    .OrderByDescending(r => r.UpdatedAt)
                    .ToList();

                Console.WriteLine($"[RequestsViewModel] Loaded {viewModels.Count} requests from Jellyseerr");
                Dispatcher.UIThread.Post(() =>
                {
                    Console.WriteLine($"[RequestsViewModel] Updating UI with {viewModels.Count} requests");
                    Requests.Clear();
                    foreach (var vm in viewModels)
                    {
                        Requests.Add(vm);
                    }

                    IsLoading = false;
                    if (isFirstLoad || viewModels.Any())
                    {
                        StatusMessage = viewModels.Any()
                            ? $"Showing {viewModels.Count} request(s)"
                            : "No active or recent requests";
                    }
                    Console.WriteLine($"[RequestsViewModel] UI update complete");
                });
                Console.WriteLine($"[RequestsViewModel] Load complete");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RequestsViewModel] Load error: {ex.Message}");
                IsLoading = false;
                StatusMessage = $"Error loading requests: {ex.Message}";
            }
        }

        private async Task PlayCompletedItem(RequestItemViewModel item)
        {
            StatusMessage = $"Searching for {item.Title} in Jellyfin...";

            try
            {
                // Search Jellyfin for the title
                var searchResults = await _dataService.SearchAsync(item.Title, CancellationToken.None);
                var matches = searchResults.ToList();

                if (matches.Any())
                {
                    // Try to find best match - prefer exact title match
                    var bestMatch = matches.FirstOrDefault(m =>
                        m.Name.Equals(item.Title, StringComparison.OrdinalIgnoreCase))
                        ?? matches.First();

                    Console.WriteLine($"[RequestsViewModel] Found match in Jellyfin: {bestMatch.Name}");
                    StatusMessage = $"Playing {bestMatch.Name}...";

                    // Trigger playback
                    PlayRequested?.Invoke(this, bestMatch);

                    // Clear status after a delay
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        Dispatcher.UIThread.Post(() => StatusMessage = "");
                    });
                }
                else
                {
                    StatusMessage = $"Could not find '{item.Title}' in Jellyfin";
                    Console.WriteLine($"[RequestsViewModel] No Jellyfin match for: {item.Title}");

                    // Clear status after delay
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        Dispatcher.UIThread.Post(() => StatusMessage = "");
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RequestsViewModel] Play error: {ex.Message}");
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        public void Dispose()
        {
            _refreshTimer?.Stop();
            _refreshTimer = null;
        }
    }
}
