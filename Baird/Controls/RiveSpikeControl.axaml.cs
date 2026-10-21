using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Baird.ViewModels;

namespace Baird.Controls
{
    public partial class RiveSpikeControl : UserControl
    {
        private RiveSpikeViewModel? _vm;

        public RiveSpikeControl()
        {
            InitializeComponent();
            DataContextChanged += (_, _) => Hook();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // Bindings are applied by now; refresh the status once more.
            Dispatcher.UIThread.Post(Push, DispatcherPriority.Loaded);
        }

        private void Hook()
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as RiveSpikeViewModel;
            if (_vm != null) { _vm.PropertyChanged += OnVmChanged; Push(); }
        }

        private void OnVmChanged(object? s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RiveSpikeViewModel.Rating)) Push();
        }

        // C# -> Rive state machine input
        private void Push()
        {
            if (_vm == null) return;
            var player = this.FindControl<Baird.Rive.RivePlayerControl>("Player");
            if (player == null) return;
            // The DataContext is set before the Source binding has reached the player, so an empty Source is
            // "not ready yet", not a failure. Only a recorded LastError is a real failure.
            if (string.IsNullOrEmpty(player.Source)) { _vm.LoadStatus = "Rive: waiting for source..."; return; }
            if (!player.IsLoaded) player.Load();
            _vm.LoadStatus = player.IsLoaded
                ? $"Rive: loaded {_vm.Source}"
                : player.LastError is { } err ? $"Rive: FAILED - {err}" : "Rive: not loaded yet";
            player.SetNumber(_vm.InputName, _vm.Rating);
        }
    }
}
