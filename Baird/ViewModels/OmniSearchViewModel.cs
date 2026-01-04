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
        public List<MediaItem> AllItems { get; set; } = new();

        public OmniSearchViewModel()
        {
            this.WhenAnyValue(x => x.SearchText)
                .Throttle(TimeSpan.FromMilliseconds(50), RxApp.MainThreadScheduler)
                .Subscribe(PerformSearch);
        }

        private void PerformSearch(string? searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                // Show all items if search is empty, or clear?
                // Typically show all for browsing
                SearchResults.Clear();
                foreach (var item in AllItems) SearchResults.Add(item);
                return;
            }

            var query = searchText.Trim();

            // Priority 1: Channel Number (Details) Prefix Match
            // Sort by length to prioritize exact/shorter matches (e.g. "3" before "30")
            var channelMatches = AllItems
                .Where(i => i.Details != null && i.Details.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.Details.Length)
                .ThenBy(i => i.Details);

            // Priority 2: Name Fuzzy Match (Contains)
            // Exclude items already found in channel matches
            // Using a simple "Contains" for fuzzy simulation here
            var nameMatches = AllItems
                .Where(i => i.Name != null && i.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(i => !i.Details.StartsWith(query, StringComparison.OrdinalIgnoreCase)); // Avoid duplicates from priority 1

            // Combine
            SearchResults.Clear();
            foreach (var item in channelMatches) SearchResults.Add(item);
            foreach (var item in nameMatches) SearchResults.Add(item);
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
    }
}
