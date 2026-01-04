using System.Collections.ObjectModel;
using ReactiveUI;
using System.Reactive;
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

        public OmniSearchViewModel()
        {
            this.WhenAnyValue(x => x.SearchText)
                .Subscribe(PerformSearch);
        }

        private void PerformSearch(string? searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                // If search is cleared, maybe we want to show all or nothing?
                // For now, let's just not filter if empty, or clear results?
                // The previous logic accumulated everything. 
                // Let's stick to the request: "initially with a dummy implementation".
                return;
            }

            // Dummy implementation
            SearchResults.Clear();
            SearchResults.Add(new MediaItem { Name = $"Search: {searchText}", Details = "Dummy Result" });
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
