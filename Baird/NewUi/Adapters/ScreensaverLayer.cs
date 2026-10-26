using Avalonia.Controls;
using Baird.Controls;
using Baird.NewUi.Lifecycle;
using Baird.Services;
using Baird.ViewModels;

namespace Baird.NewUi.Adapters
{
    /// <summary>
    /// The classic screensaver (aerial videos, its own player), shown over everything. The control, and its player, exist
    /// only while it is up.
    /// </summary>
    public sealed class ScreensaverLayer : IScreensaver
    {
        private readonly ScreensaverViewModel _vm;
        public Panel View { get; } = new() { IsHitTestVisible = false };

        public ScreensaverLayer(ScreensaverService service) => _vm = new ScreensaverViewModel(service);

        public bool IsActive => _vm.IsActive;

        public void Activate()
        {
            _vm.Activate();
            if (_vm.IsActive && View.Children.Count == 0) View.Children.Add(new ScreensaverControl { DataContext = _vm });
        }

        public void Deactivate()
        {
            _vm.Deactivate();
            View.Children.Clear();
        }
    }
}
