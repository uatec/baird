using System.Collections.ObjectModel;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using Baird.Services;
using System;
using System.Threading.Tasks;
using System.Linq;
using DynamicData;

namespace Baird.ViewModels
{
    public class WatchlistViewModel : ReactiveObject
    {
        private readonly IDataService _dataService;
        public ObservableCollection<MediaItemViewModel> WatchlistItems { get; } = new();
        public ObservableCollection<MediaRowViewModel> WatchlistRows { get; } = new();

        public event EventHandler<MediaItemViewModel>? PlayRequested;
        public event EventHandler? BackRequested;

        public ReactiveCommand<MediaItemViewModel, Unit> PlayCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }
        public ReactiveCommand<MediaItemViewModel, Unit> RemoveCommand { get; }

        public WatchlistViewModel(IDataService dataService)
        {
            _dataService = dataService;

            PlayCommand = ReactiveCommand.Create<MediaItemViewModel>(item =>
            {
                PlayRequested?.Invoke(this, item);
            });

            BackCommand = ReactiveCommand.Create(() =>
            {
                BackRequested?.Invoke(this, EventArgs.Empty);
            });

            RemoveCommand = ReactiveCommand.CreateFromTask<MediaItemViewModel>(async item =>
            {
                await _dataService.RemoveFromWatchlistAsync(item.Id);
                // The list will update via event or RefreshAsync
                // We can manually remove it for instant feedback if we want, 
                // but RefreshAsync is safer for sync. 
                // However, since we listen to WatchlistUpdated in DataService/MainViewModel?
                // Actually WatchlistViewModel should listen to DataService events.
            });

            // Subscribe to updates
            _dataService.WatchlistUpdated += async (s, e) =>
            {
                // Dispatch to UI thread if needed
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshAsync);
            };
        }

        public async Task RefreshAsync()
        {
            // Fetch latest watchlist
            var newItems = await _dataService.GetWatchlistItemsAsync();

            // Sync with existing collection
            var toRemove = WatchlistItems.Where(i => !newItems.Any(n => n.Id == i.Id)).ToList();
            foreach (var item in toRemove)
            {
                WatchlistItems.Remove(item);
            }

            int index = 0;
            foreach (var newItem in newItems)
            {
                var existing = WatchlistItems.FirstOrDefault(x => x.Id == newItem.Id);
                if (existing != null)
                {
                    // Update properties if needed
                    if (existing.History != newItem.History)
                    {
                        existing.History = newItem.History;
                    }

                    // Order
                    int oldIndex = WatchlistItems.IndexOf(existing);
                    if (oldIndex != index)
                    {
                        WatchlistItems.Move(oldIndex, index);
                    }
                }
                else
                {
                    WatchlistItems.Insert(index, newItem);
                }
                index++;
            }

            // Update row collection for virtualization
            UpdateWatchlistRows();
        }

        private void UpdateWatchlistRows()
        {
            var rows = MediaRowViewModel.CreateRows(WatchlistItems);

            // Incrementally update rows to avoid disruption
            // Remove excess rows if list shrunk
            while (WatchlistRows.Count > rows.Length)
            {
                WatchlistRows.RemoveAt(WatchlistRows.Count - 1);
            }

            // Update existing rows and add new ones
            for (int i = 0; i < rows.Length; i++)
            {
                if (i < WatchlistRows.Count)
                {
                    // Replace existing row if items changed
                    if (!AreSameRowItems(WatchlistRows[i], rows[i]))
                    {
                        WatchlistRows[i] = rows[i];
                    }
                }
                else
                {
                    // Add new row
                    WatchlistRows.Add(rows[i]);
                }
            }
        }

        private bool AreSameRowItems(MediaRowViewModel row1, MediaRowViewModel row2)
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
