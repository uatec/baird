using System.Collections.ObjectModel;
using Baird.Services;
using ReactiveUI;

namespace Baird.ViewModels;

public class ProgrammeDetailViewModel : ReactiveObject
{
    private readonly IDataService _dataService;

    private MediaItem _selectedProgramme;
    public MediaItem SelectedProgramme
    {
        get => _selectedProgramme;
        set => this.RaiseAndSetIfChanged(ref _selectedProgramme, value);
    }

    public ObservableCollection<MediaItem> ProgrammeChildren { get; } = new();

    public event EventHandler<MediaItem>? PlayRequested;
    public event EventHandler? BackRequested;

    public ReactiveCommand<MediaItem, System.Reactive.Unit> PlayCommand { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> BackCommand { get; }

    public void RequestPlay(MediaItem item)
    {
        PlayRequested?.Invoke(this, item);
    }

    public void RequestBack()
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    public ProgrammeDetailViewModel(IDataService dataService, MediaItem programme)
    {
        _dataService = dataService;
        _selectedProgramme = programme;

        PlayCommand = ReactiveCommand.Create<MediaItem>(RequestPlay);
        BackCommand = ReactiveCommand.Create(RequestBack);

        _ = LoadChildren();
    }

    private async Task LoadChildren()
    {
        ProgrammeChildren.Clear();

        try
        {
            IEnumerable<MediaItem> children = await _dataService.GetChildrenAsync(SelectedProgramme.Id);
            foreach (MediaItem item in children)
            {
                ProgrammeChildren.Add(item);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching children: {ex.Message}");
        }
    }
}
