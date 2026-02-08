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
        private readonly IEnumerable<IMediaProvider> _providers;

        private MediaItem _selectedProgramme;
        public MediaItem SelectedProgramme
        {
            get => _selectedProgramme;
            set => this.RaiseAndSetIfChanged(ref _selectedProgramme, value);
        }

        public ObservableCollection<MediaItem> ProgrammeChildren { get; } = new();

        public event EventHandler<MediaItem>? PlayRequested;
        public event EventHandler? BackRequested;

        public void RequestPlay(MediaItem item)
        {
            PlayRequested?.Invoke(this, item);
        }

        public void RequestBack()
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        public ProgrammeDetailViewModel(IEnumerable<IMediaProvider> providers, MediaItem programme)
        {
            _providers = providers;
            SelectedProgramme = programme;
            _ = LoadChildren();
        }

        private async Task LoadChildren()
        {
            ProgrammeChildren.Clear();
            
            var tasks = new List<Task<IEnumerable<MediaItem>>>();
            foreach(var p in _providers)
            {
               tasks.Add(p.GetChildrenAsync(SelectedProgramme.Id));
            }

            try 
            {
                var results = await Task.WhenAll(tasks);
                foreach(var list in results)
                {
                    foreach(var item in list)
                    {
                        ProgrammeChildren.Add(item);
                    }
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Error fetching children: {ex.Message}");
            }
        }
    }
}
