using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Baird.ViewModels;

namespace Baird.Controls
{
    public partial class EpgControl : UserControl
    {
        public EpgControl()
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

            // Trigger data refresh if channels are empty
            if (DataContext is EpgViewModel vm && vm.Channels.Count == 0)
            {
                _ = vm.RefreshAsync();
            }

            // Set initial focus to the channels list
            var channelsList = this.FindControl<ListBox>("ChannelsList");
            channelsList?.Focus();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.Handled) return;

            if (DataContext is EpgViewModel filterVm)
            {
                // Remote number keys filter the channels by number; Backspace takes the last digit off.
                if (TryGetDigit(e.Key) is { } digit)
                {
                    filterVm.AppendChannelDigit(digit);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Back && filterVm.BackspaceChannelDigit())
                {
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == Key.Enter)
            {
                if (DataContext is EpgViewModel vm && vm.SelectedChannel != null)
                {
                    vm.PlayChannelCommand.Execute(vm.SelectedChannel.Channel).Subscribe();
                    e.Handled = true;
                }
            }
        }

        private static string? TryGetDigit(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9) return ((int)key - (int)Key.D0).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return ((int)key - (int)Key.NumPad0).ToString();
            return null;
        }
    }
}
