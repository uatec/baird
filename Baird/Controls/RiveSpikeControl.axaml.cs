using System.ComponentModel;
using Avalonia.Controls;
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
            if (!player.IsLoaded) player.Load();
            _vm.LoadStatus = player.IsLoaded
                ? $"Rive: loaded {_vm.Source}"
                : $"Rive: FAILED - {player.LastError ?? "unknown error"}";
            player.SetNumber(_vm.InputName, _vm.Rating);
        }
    }
}
