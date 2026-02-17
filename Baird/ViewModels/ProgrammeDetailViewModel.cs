using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Baird.Services;
using ReactiveUI;

namespace Baird.ViewModels
{
    public class ProgrammeDetailViewModel : ReactiveObject
    {
        private readonly IDataService _dataService;

        private MediaItemViewModel _selectedProgramme;
        public MediaItemViewModel SelectedProgramme
        {
            get => _selectedProgramme;
            set => this.RaiseAndSetIfChanged(ref _selectedProgramme, value);
        }

        public ObservableCollection<MediaItemViewModel> ProgrammeChildren { get; } = new();

        public event EventHandler<MediaItemViewModel>? PlayRequested;
        public event EventHandler? BackRequested;

        public ReactiveCommand<MediaItemViewModel, System.Reactive.Unit> PlayCommand { get; }
        public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> BackCommand { get; }
        public ReactiveCommand<MediaItemViewModel, System.Reactive.Unit> AddToWatchlistCommand { get; }

        public void RequestPlay(MediaItemViewModel item)
        {
            PlayRequested?.Invoke(this, item);
        }

        public void RequestBack()
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        public ProgrammeDetailViewModel(IDataService dataService, MediaItemViewModel programme)
        {
            _dataService = dataService;
            _selectedProgramme = programme;

            PlayCommand = ReactiveCommand.Create<MediaItemViewModel>(RequestPlay);
            BackCommand = ReactiveCommand.Create(RequestBack);

            AddToWatchlistCommand = ReactiveCommand.CreateFromTask<MediaItemViewModel>(async item =>
            {
                await _dataService.AddToWatchlistAsync(item);
                Console.WriteLine($"[ProgrammeDetail] Added {item.Name} to watchlist");
            });

            _ = LoadChildren();
        }

        private async Task LoadChildren()
        {
            ProgrammeChildren.Clear();

            try
            {
                var children = await _dataService.GetChildrenAsync(SelectedProgramme.Id);
                foreach (var item in children)
                {
                    ProgrammeChildren.Add(item);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProgrammeDetailViewModel] Error fetching children: {ex.Message}");
            }
        }
    }
}
