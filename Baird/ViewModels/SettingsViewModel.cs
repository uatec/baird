using ReactiveUI;
using System;
using System.Reactive;

namespace Baird.ViewModels
{
    public class SettingsViewModel : ReactiveObject
    {
        private double _uiScale = 1.0;

        // Boundaries for scaling
        private const double MinScale = 0.5;
        private const double MaxScale = 2.0;
        private const double ScaleStep = 0.1;

        public double UiScale
        {
            get => _uiScale;
            set => this.RaiseAndSetIfChanged(ref _uiScale, Math.Round(value, 1));
        }

        public ReactiveCommand<Unit, Unit> IncreaseScaleCommand { get; }
        public ReactiveCommand<Unit, Unit> DecreaseScaleCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        public event EventHandler? BackRequested;

        public SettingsViewModel()
        {
            IncreaseScaleCommand = ReactiveCommand.Create(() =>
            {
                if (UiScale < MaxScale)
                {
                    UiScale += ScaleStep;
                }
            });

            DecreaseScaleCommand = ReactiveCommand.Create(() =>
            {
                if (UiScale > MinScale)
                {
                    UiScale -= ScaleStep;
                }
            });

            BackCommand = ReactiveCommand.Create(() =>
            {
                BackRequested?.Invoke(this, EventArgs.Empty);
            });
        }
    }
}
