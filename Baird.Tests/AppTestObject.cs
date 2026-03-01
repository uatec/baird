using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Baird.Controls;
using Baird.Models;
using Baird.Services;
using Baird.ViewModels;
using Baird.Tests.Mocks;
using ReactiveUI;

namespace Baird.Tests
{
    /// <summary>
    /// Page Object wrapper for UI Automation Tests to describe user experience.
    /// </summary>
    public class AppTestObject
    {
        public MainViewModel ViewModel { get; }
        public TabNavigationControl TabNavigationControl { get; }

        private readonly MockMediaProvider _provider;
        private readonly MockHistoryService _historyService;

        private AppTestObject()
        {
            // Set up minimal mocks to instantiate MainViewModel
            _provider = new MockMediaProvider();
            _historyService = new MockHistoryService();
            var searchHistoryService = new MockSearchHistoryService();
            var watchlistService = new MockWatchlistService();
            var dataService = new DataService(
                new[] { _provider },
                _historyService,
                watchlistService,
                new MediaItemCache(),
                new MockMediaDataCache());

            var cecService = new MockCecService();
            var jellyseerrService = new MockJellyseerrService();
            var screensaverService = new ScreensaverService();

            ViewModel = new MainViewModel(null, dataService, searchHistoryService, screensaverService, cecService, jellyseerrService);

            // Connect the View to the ViewModel
            TabNavigationControl = new TabNavigationControl
            {
                DataContext = ViewModel.MainMenu
            };
        }

        public static AppTestObject Create()
        {
            return new AppTestObject();
        }

        public void SetupProviderData(string parentId, List<MediaItemData> children)
        {
            _provider.SetChildren(parentId, children);
        }

        public MainMenuTestObject OpenMainMenu()
        {
            ViewModel.OpenMainMenu();
            return new MainMenuTestObject(TabNavigationControl, ViewModel);
        }

        public VideoPlayerTestObject VideoPlayer => new VideoPlayerTestObject(ViewModel);

        public void PushView(ReactiveObject viewModel)
        {
            ViewModel.PushViewModel(viewModel);
        }

        public int NavigationStackCount => ViewModel.NavigationHistory.Count;

        public object? CurrentView => ViewModel.CurrentPage;

        public MediaItemViewModel? CurrentActiveItem => ViewModel.ActiveItem;
    }

    public class MainMenuTestObject
    {
        private readonly TabNavigationControl _tabNavigationControl;
        private readonly MainViewModel _mainViewModel;

        public MainMenuTestObject(TabNavigationControl tabNavigationControl, MainViewModel mainViewModel)
        {
            _tabNavigationControl = tabNavigationControl;
            _mainViewModel = mainViewModel;
        }

        public void PressUpOnTab()
        {
            // Since Avalonia XAML is not evaluated in basic xUnit tests, 
            // we simulate the key press on the TabButton which the XAML would normally route.
            var tabs = _mainViewModel.MainMenu.Tabs;
            if (tabs.Count == 0) return;

            var button = new Button { DataContext = tabs[0] };

            var methodInfo = typeof(TabNavigationControl).GetMethod("OnTabButtonKeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var keyEventArgs = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Up,
                Source = button
            };

            methodInfo?.Invoke(_tabNavigationControl, new object[] { button, keyEventArgs });
        }
    }

    public class VideoPlayerTestObject
    {
        private readonly MainViewModel _mainViewModel;

        public VideoPlayerTestObject(MainViewModel mainViewModel)
        {
            _mainViewModel = mainViewModel;
        }

        public void PlayItem(MediaItemData item)
        {
            _mainViewModel.PlayItem(new MediaItemViewModel(item));
        }

        public void SetCurrentEpisodeContext(List<MediaItemData> episodes, string seasonId, string showId)
        {
            var episodeViewModels = episodes.Select(e => new MediaItemViewModel(e)).ToList();

            var currentEpisodeListField = typeof(MainViewModel).GetField("_currentEpisodeList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentEpisodeListField?.SetValue(_mainViewModel, episodeViewModels);

            var currentSeasonIdField = typeof(MainViewModel).GetField("_currentSeasonId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentSeasonIdField?.SetValue(_mainViewModel, seasonId);

            var currentShowIdField = typeof(MainViewModel).GetField("_currentShowId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            currentShowIdField?.SetValue(_mainViewModel, showId);
        }

        public async Task SimulatePlaybackEndingAndPlayNext()
        {
            var playNextMethod = typeof(MainViewModel).GetMethod("PlayNextEpisodeOrGoBack", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (playNextMethod != null)
            {
                var task = playNextMethod.Invoke(_mainViewModel, null) as Task;
                if (task != null)
                {
                    await task;
                }
            }
        }
    }

    // Helper classes for minimal mocking
    internal class MockMediaProvider : IMediaProvider
    {
        public string Name => "Test Provider";
        private readonly Dictionary<string, List<MediaItemData>> _childrenMap = new();

        public void SetChildren(string parentId, List<MediaItemData> children)
        {
            _childrenMap[parentId] = children;
        }

        public Task InitializeAsync() => Task.CompletedTask;
        public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult(Enumerable.Empty<MediaItemData>());
        public Task<IEnumerable<MediaItemData>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<MediaItemData>());

        public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id)
        {
            if (_childrenMap.TryGetValue(id, out var children))
            {
                return Task.FromResult((IEnumerable<MediaItemData>)children);
            }
            return Task.FromResult(Enumerable.Empty<MediaItemData>());
        }

        public Task<MediaItemData?> GetItemAsync(string id) => Task.FromResult<MediaItemData?>(null);
    }

    internal class MockHistoryService : IHistoryService
    {
        public Task<List<HistoryItem>> GetHistoryAsync() => Task.FromResult(new List<HistoryItem>());
        public Task UpsertAsync(MediaItemViewModel item, TimeSpan position, TimeSpan duration) => Task.CompletedTask;
        public HistoryItem? GetProgress(string id) => null;
        public Task ClearHistoryAsync() => Task.CompletedTask;
    }

    internal class MockSearchHistoryService : ISearchHistoryService
    {
        public Task AddSearchTermAsync(string term) => Task.CompletedTask;
        public Task<IEnumerable<string>> GetSuggestedTermsAsync(int maxCount) => Task.FromResult(Enumerable.Empty<string>());
    }

    internal class MockWatchlistService : IWatchlistService
    {
        public event EventHandler? WatchlistUpdated;
        public Task AddAsync(string id) => Task.CompletedTask;
        public Task RemoveAsync(string id) => Task.CompletedTask;
        public Task<HashSet<string>> GetWatchlistIdsAsync() => Task.FromResult(new HashSet<string>());
        public bool IsOnWatchlist(string id) => false;
    }

    internal class MockJellyseerrService : IJellyseerrService
    {
        public Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string query, int page, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JellyseerrSearchResult>());
        public Task<JellyseerrRequestResponse> CreateRequestAsync(int mediaId, string mediaType, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(new JellyseerrRequestResponse { Success = false });
        public Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JellyseerrRequest>());
        public Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int page, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JellyseerrSearchResult>());
    }
}
