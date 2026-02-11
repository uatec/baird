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
        }

        public async Task RefreshAsync()
        {
            HistoryItems.Clear();
            var items = await _dataService.GetHistoryItemsAsync();

            foreach (var item in items)
            {
                HistoryItems.Add(item);
            }
        }
    }
}
