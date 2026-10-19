using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Baird.ViewModels;

namespace Baird.Controls
{
    public partial class EpgOverlayControl : UserControl
    {
        public EpgOverlayControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            var list = this.FindControl<ListBox>("OverlayChannelsList");
            list?.Focus();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            {
                var list = this.FindControl<ListBox>("OverlayChannelsList");
                list?.Focus();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Handled) return;

            if (e.Key == Key.Enter)
            {
                if (DataContext is MainViewModel vm && vm.Epg.SelectedChannel != null)
                {
                    vm.Epg.PlayChannelCommand.Execute(vm.Epg.SelectedChannel.Channel).Subscribe();
                    vm.IsEpgActive = false;
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape || e.Key == Key.BrowserBack || e.Key == Key.G)
            {
                if (DataContext is MainViewModel vm)
                {
                    vm.IsEpgActive = false;
                    e.Handled = true;
                }
            }
        }
    }
}
