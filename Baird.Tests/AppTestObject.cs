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

namespace Baird.Tests
{
    /// <summary>
    /// Page Object wrapper for UI Automation Tests to describe user experience.
    /// </summary>
    public class AppTestObject
    {
        public MainViewModel ViewModel { get; }
        public TabNavigationControl TabNavigationControl { get; }

        private AppTestObject()
        {
            // Set up minimal mocks to instantiate MainViewModel
            var provider = new MockMediaProvider();
            var historyService = new MockHistoryService();
            var searchHistoryService = new MockSearchHistoryService();
            var watchlistService = new MockWatchlistService();
            var dataService = new DataService(
                new[] { provider },
                historyService,
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

        public void OpenMainMenu()
        {
            ViewModel.OpenMainMenu();
        }

        public void PressUpOnTab()
        {
            // Since Avalonia XAML is not evaluated in basic xUnit tests, 
            // we simulate the key press on the TabButton which the XAML would normally route.
            var tabs = ViewModel.MainMenu.Tabs;
            if (tabs.Count == 0) return;

            var button = new Button { DataContext = tabs[0] };

            var methodInfo = typeof(TabNavigationControl).GetMethod("OnTabButtonKeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            var keyEventArgs = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.Up,
                Source = button
            };

            methodInfo?.Invoke(TabNavigationControl, new object[] { button, keyEventArgs });
        }

        public object? CurrentView => ViewModel.CurrentPage;

        // Helper classes for minimal mocking
        private class MockMediaProvider : IMediaProvider
        {
            public string Name => "Test Provider";
            public Task InitializeAsync() => Task.CompletedTask;
            public Task<IEnumerable<MediaItemData>> GetListingAsync() => Task.FromResult(Enumerable.Empty<MediaItemData>());
            public Task<IEnumerable<MediaItemData>> SearchAsync(string query, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<MediaItemData>());
            public Task<IEnumerable<MediaItemData>> GetChildrenAsync(string id) => Task.FromResult(Enumerable.Empty<MediaItemData>());
            public Task<MediaItemData?> GetItemAsync(string id) => Task.FromResult<MediaItemData?>(null);
        }

        private class MockHistoryService : IHistoryService
        {
            public Task<List<HistoryItem>> GetHistoryAsync() => Task.FromResult(new List<HistoryItem>());
            public Task UpsertAsync(MediaItemViewModel item, TimeSpan position, TimeSpan duration) => Task.CompletedTask;
            public HistoryItem? GetProgress(string id) => null;
            public Task ClearHistoryAsync() => Task.CompletedTask;
        }

        private class MockSearchHistoryService : ISearchHistoryService
        {
            public Task AddSearchTermAsync(string term) => Task.CompletedTask;
            public Task<IEnumerable<string>> GetSuggestedTermsAsync(int maxCount) => Task.FromResult(Enumerable.Empty<string>());
        }

        private class MockWatchlistService : IWatchlistService
        {
            public event EventHandler? WatchlistUpdated;
            public Task AddAsync(string id) => Task.CompletedTask;
            public Task RemoveAsync(string id) => Task.CompletedTask;
            public Task<HashSet<string>> GetWatchlistIdsAsync() => Task.FromResult(new HashSet<string>());
            public bool IsOnWatchlist(string id) => false;
        }

        private class MockJellyseerrService : IJellyseerrService
        {
            public Task<IEnumerable<JellyseerrSearchResult>> SearchAsync(string query, int page, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JellyseerrSearchResult>());
            public Task<JellyseerrRequestResponse> CreateRequestAsync(int mediaId, string mediaType, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(new JellyseerrRequestResponse { Success = false });
            public Task<IEnumerable<JellyseerrRequest>> GetRequestsAsync(System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JellyseerrRequest>());
            public Task<IEnumerable<JellyseerrSearchResult>> GetTrendingAsync(int page, System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(Enumerable.Empty<JellyseerrSearchResult>());
        }
    }
}
