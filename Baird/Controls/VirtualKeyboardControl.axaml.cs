using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty && change.NewValue is true)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var keyF = this.FindControl<Button>("KeyF");
                    keyF?.Focus();
                }, DispatcherPriority.Input);
            }
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
