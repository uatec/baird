using Avalonia.Controls;
using Avalonia.Input;
using Baird.ViewModels;

namespace Baird.Controls
{
    public partial class SettingsControl : UserControl
    {
        public SettingsControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        }

        private void ScaleControl_KeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is SettingsViewModel viewModel)
            {
                if (e.Key == Key.Left)
                {
                    viewModel.DecreaseScaleCommand.Execute().Subscribe();
                    e.Handled = true;
                }
                else if (e.Key == Key.Right)
                {
                    viewModel.IncreaseScaleCommand.Execute().Subscribe();
                    e.Handled = true;
                }
            }
        }
    }
}
