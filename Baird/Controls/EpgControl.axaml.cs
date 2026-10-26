using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Baird.ViewModels;

namespace Baird.Controls
{
    public partial class EpgControl : UserControl
    {
        public EpgControl()
        {
            InitializeComponent();

            // Tunnel so the hop to the Watch button runs before XY focus picks a neighbour.
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        }

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Right) return;

            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
            var channels = this.FindControl<ListBox>("ChannelsList");
            var schedule = this.FindControl<ListBox>("ScheduleList");
            var watch = this.FindControl<Button>("WatchButton");
            if (focused == null || watch is not { IsVisible: true, IsEffectivelyEnabled: true }) return;

            // Right off the schedule goes to Watch; from the channels it does too when there is no schedule to step into.
            var inSchedule = schedule != null && (focused == schedule || schedule.IsVisualAncestorOf(focused));
            var inChannels = channels != null && (focused == channels || channels.IsVisualAncestorOf(focused));
            if (inSchedule || (inChannels && schedule?.ItemCount is null or 0))
            {
                watch.Focus();
                e.Handled = true;
            }
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
