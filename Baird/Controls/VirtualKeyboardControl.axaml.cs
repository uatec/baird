using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;

namespace Baird.Controls
{
    public partial class VirtualKeyboardControl : UserControl
    {
        public event Action<string>? KeyPressed;
        public event Action? BackspacePressed;
        public event Action? EnterPressed;

        public VirtualKeyboardControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnKeyClick(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Content is string key)
            {
                KeyPressed?.Invoke(key);
            }
        }

        private void OnSpaceClick(object? sender, RoutedEventArgs e)
        {
            KeyPressed?.Invoke(" ");
        }

        private void OnBackspaceClick(object? sender, RoutedEventArgs e)
        {
            BackspacePressed?.Invoke();
        }

        private void OnEnterClick(object? sender, RoutedEventArgs e)
        {
            EnterPressed?.Invoke();
        }
    }
}
