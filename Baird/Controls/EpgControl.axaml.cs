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

            if (e.Key == Key.Enter)
            {
                if (DataContext is EpgViewModel vm && vm.SelectedChannel != null)
                {
                    vm.PlayChannelCommand.Execute(vm.SelectedChannel.Channel).Subscribe();
                    e.Handled = true;
                }
            }
        }
    }
}
