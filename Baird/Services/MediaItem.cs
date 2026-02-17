using System;
using Baird.Models;

namespace Baird.Services
{
    public enum MediaType
    {
        Video,
        Audio,
        Brand,
        Folder,
        Channel,
    }

    public class MediaItem : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }

        // Constructor for creating MediaItem from MediaItemData (temporary adapter)
        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        public MediaItem(MediaItemData data)
        {
            Id = data.Id;
            Name = data.Name;
            Details = data.Details;
            ImageUrl = data.ImageUrl;
            IsLive = data.IsLive;
            StreamUrl = data.StreamUrl;
            Source = data.Source;
            ChannelNumber = data.ChannelNumber;
            Type = data.Type;
            Synopsis = data.Synopsis;
            Subtitle = data.Subtitle;
            Duration = data.Duration;
        }

        // Parameterless constructor for object initializers
        public MediaItem() { }

        public required string Id { get; set; }
        public required string Name { get; set; }
        public required string Details { get; set; } // e.g. Year, Channel Number
        public required string ImageUrl { get; set; }
        public required bool IsLive { get; set; }
        public string? StreamUrl { get; set; } // Can be empty if Type is Brand
        public required string Source { get; set; } // e.g. "Live TV", "Jellyfin: home", "YouTube"
        public string? ChannelNumber { get; set; }

        public required MediaType Type { get; set; }
        public required string Synopsis { get; set; } // keep this?
        public required string Subtitle { get; set; } // e.g. "Series 1: Episode 1"

        public Baird.Models.HistoryItem? History { get; set; }

        private bool _isOnWatchlist;
        public bool IsOnWatchlist
        {
            get => _isOnWatchlist;
            set
            {
                if (_isOnWatchlist != value)
                {
                    _isOnWatchlist = value;
                    OnPropertyChanged();
                }
            }
        }

        public TimeSpan Duration { get; set; }
        public bool HasDuration => Duration > TimeSpan.Zero;

        // Computed properties for compatibility and ease of binding
        public bool IsFinished
        {
            get => History?.IsFinished ?? false;
            set
            {
                if (History == null)
                {
                    History = new Baird.Models.HistoryItem
                    {
                        Id = Id,
                        LastPosition = value ? Duration : TimeSpan.Zero,
                        Duration = Duration,
                        IsFinished = value,
                        LastWatched = DateTime.Now
                    };
                }
                else
                {
                    History.IsFinished = value;
                }
                OnPropertyChanged();
            }
        }

        public TimeSpan LastPosition
        {
            get => History?.LastPosition ?? TimeSpan.Zero;
            set
            {
                if (History == null)
                {
                    History = new Baird.Models.HistoryItem
                    {
                        Id = Id,
                        LastPosition = value,
                        Duration = Duration,
                        IsFinished = false,
                        LastWatched = DateTime.Now
                    };
                }
                else
                {
                    History.LastPosition = value;
                }
                OnPropertyChanged();
            }
        }

        public double Progress => History?.Progress ?? 0;
    }
}
