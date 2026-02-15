using System.Collections.ObjectModel;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using Baird.Services;
using System;
using System.Threading.Tasks;
using System.Linq;

namespace Baird.ViewModels
{
    public class HistoryViewModel : ReactiveObject
    {
        private readonly IDataService _dataService;
        public ObservableCollection<MediaItem> HistoryItems { get; } = new();

        public event EventHandler<MediaItem>? PlayRequested;
        public event EventHandler? BackRequested;

        public ReactiveCommand<MediaItem, Unit> PlayCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }
        public ReactiveCommand<MediaItem, Unit> AddToWatchlistCommand { get; }

        public HistoryViewModel(IDataService dataService)
        {
            _dataService = dataService;

            PlayCommand = ReactiveCommand.Create<MediaItem>(item =>
            {
                PlayRequested?.Invoke(this, item);
            });

            BackCommand = ReactiveCommand.Create(() =>
            {
                BackRequested?.Invoke(this, EventArgs.Empty);
            });

            AddToWatchlistCommand = ReactiveCommand.CreateFromTask<MediaItem>(async item =>
            {
                await _dataService.AddToWatchlistAsync(item);
                Console.WriteLine($"[History] Added {item.Name} to watchlist");
            });
        }

        public async Task RefreshAsync()
        {
            // Fetch latest history
            var newItems = await _dataService.GetHistoryItemsAsync();

            // Sync with existing collection to avoid full clear/add (reduces flicker)
            // 1. Remove items not in new list
            var toRemove = HistoryItems.Where(i => !newItems.Any(n => n.Id == i.Id)).ToList();
            foreach (var item in toRemove)
            {
                HistoryItems.Remove(item);
            }

            // 2. Add or update items
            int index = 0;
            foreach (var newItem in newItems)
            {
                // If item exists, update properties if needed (e.g. progress)
                // Since MediaItem is a class and GetHistoryItemsAsync wraps the same objects if cached in DataService,
                // we might have reference equality if we are lucky, or ID match.

                var existing = HistoryItems.FirstOrDefault(x => x.Id == newItem.Id);
                if (existing != null)
                {
                    // Update history property if reference changed (unlikely with DataService cache, but good for safety)
                    if (existing.History != newItem.History)
                    {
                        existing.History = newItem.History;
                    }

                    // Ensure order
                    int oldIndex = HistoryItems.IndexOf(existing);
                    if (oldIndex != index)
                    {
                        HistoryItems.Move(oldIndex, index);
                    }
                }
                else
                {
                    HistoryItems.Insert(index, newItem);
                }
                index++;
            }
        }
    }
}
