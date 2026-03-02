using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
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

        private void InputDetection_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Up || e.Key == Key.Down || e.Key == Key.Left || e.Key == Key.Right)
                return;

            if (DataContext is SettingsViewModel viewModel)
            {
                var keyStr = e.KeyModifiers != KeyModifiers.None
                    ? $"{e.KeyModifiers}+{e.Key}"
                    : e.Key.ToString();
                viewModel.LogKeyPress(keyStr);
                e.Handled = true;
            }
        }

        private void InputDetection_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (DataContext is SettingsViewModel viewModel)
            {
                var props = e.GetCurrentPoint(sender as Avalonia.Visual).Properties;
                string buttonName;
                if (props.IsLeftButtonPressed) buttonName = "Mouse: Left";
                else if (props.IsRightButtonPressed) buttonName = "Mouse: Right";
                else if (props.IsMiddleButtonPressed) buttonName = "Mouse: Middle";
                else if (props.IsXButton1Pressed) buttonName = "Mouse: XButton1";
                else if (props.IsXButton2Pressed) buttonName = "Mouse: XButton2";
                else buttonName = "Mouse: Unknown";
                viewModel.LogKeyPress(buttonName);
                e.Handled = true;
            }
        }
    }
}
