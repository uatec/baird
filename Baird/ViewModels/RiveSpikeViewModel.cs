using System;
using System.Reactive;
using ReactiveUI;

namespace Baird.ViewModels
{
    public class RiveSpikeViewModel : ReactiveObject
    {
        private int _rating = 3;

        public event EventHandler? BackRequested;

        public string Source => "assets/rive/rating.riv";
        public string StateMachine => "State Machine 1";
        public string InputName => "rating";

        /// <summary>Drives the "rating" number input of rating.riv (0..5).</summary>
        public int Rating
        {
            get => _rating;
            set => this.RaiseAndSetIfChanged(ref _rating, Math.Clamp(value, 0, 5));
        }

        public ReactiveCommand<Unit, Unit> BackCommand { get; }
        public ReactiveCommand<Unit, Unit> IncrementCommand { get; }
        public ReactiveCommand<Unit, Unit> DecrementCommand { get; }

        public RiveSpikeViewModel()
        {
            BackCommand = ReactiveCommand.Create(() => BackRequested?.Invoke(this, EventArgs.Empty));
            IncrementCommand = ReactiveCommand.Create(() => { Rating++; });
            DecrementCommand = ReactiveCommand.Create(() => { Rating--; });
        }
    }
}
