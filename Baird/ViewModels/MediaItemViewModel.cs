using System;
using Baird.Models;
using Baird.Services;
using ReactiveUI;

namespace Baird.ViewModels
{
    /// <summary>
    /// ViewModel for MediaItem that combines data (MediaItemData) with UI state (History, IsOnWatchlist).
    /// Uses ReactiveObject for proper change notification compatible with ReactiveUI ViewModels.
    /// </summary>
    public class MediaItemViewModel : ReactiveObject
    {
        private MediaItemData _data;
        private HistoryItem? _history;
        private bool _isOnWatchlist;

        public MediaItemViewModel(MediaItemData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        /// <summary>
        /// Replaces the underlying data (e.g. after a background refresh from a provider)
        /// and raises property-changed notifications so the UI updates.
        /// </summary>
        public void UpdateData(MediaItemData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            this.RaisePropertyChanged(nameof(Name));
            this.RaisePropertyChanged(nameof(Details));
            this.RaisePropertyChanged(nameof(ImageUrl));
            this.RaisePropertyChanged(nameof(IsLive));
            this.RaisePropertyChanged(nameof(StreamUrl));
            this.RaisePropertyChanged(nameof(Source));
            this.RaisePropertyChanged(nameof(ChannelNumber));
            this.RaisePropertyChanged(nameof(Type));
            this.RaisePropertyChanged(nameof(Synopsis));
            this.RaisePropertyChanged(nameof(Subtitle));
            this.RaisePropertyChanged(nameof(Duration));
            this.RaisePropertyChanged(nameof(HasDuration));
        }

        // Data properties - pass-through from MediaItemData
        public string Id => _data.Id;
        public string Name => _data.Name;
        public string Details => _data.Details;
        public string ImageUrl => _data.ImageUrl;
        public bool IsLive => _data.IsLive;
        public string? StreamUrl => _data.StreamUrl;
        public string Source => _data.Source;
        public string? ChannelNumber => _data.ChannelNumber;
        public MediaType Type => _data.Type;
        public string Synopsis => _data.Synopsis;
        public string Subtitle => _data.Subtitle;
        public TimeSpan Duration => _data.Duration;
        public bool HasDuration => _data.HasDuration;

        // UI state properties - reactive
        public HistoryItem? History
        {
            get => _history;
            set
            {
                this.RaiseAndSetIfChanged(ref _history, value);
                // Raise notifications for computed properties
                this.RaisePropertyChanged(nameof(IsFinished));
                this.RaisePropertyChanged(nameof(LastPosition));
                this.RaisePropertyChanged(nameof(Progress));
            }
        }

        public bool IsOnWatchlist
        {
            get => _isOnWatchlist;
            set => this.RaiseAndSetIfChanged(ref _isOnWatchlist, value);
        }

        // Computed properties based on History
        public bool IsFinished => History?.IsFinished ?? false;
        public TimeSpan LastPosition => History?.LastPosition ?? TimeSpan.Zero;
        public double Progress => History?.Progress ?? 0;
    }
}
