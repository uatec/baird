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
        }

        public void AppendDigit(string digit)
        {
            SearchText += digit;
            // TODO: Trigger search logic here
            SearchResults.Add(new MediaItem { Name = $"Result for {SearchText}", Details = "Channel" });
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
