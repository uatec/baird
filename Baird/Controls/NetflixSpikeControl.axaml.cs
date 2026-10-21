using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Baird.ViewModels;

namespace Baird.Controls
{
    public partial class NetflixSpikeControl : UserControl
    {
        private VirtualKeyboardControl? _virtualKeyboard;
        private ScrollViewer? _logScrollViewer;

        public NetflixSpikeControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            _virtualKeyboard = this.FindControl<VirtualKeyboardControl>("MfaVirtualKeyboard");
            _logScrollViewer = this.FindControl<ScrollViewer>("LogScrollViewer");

            if (_virtualKeyboard != null)
            {
                _virtualKeyboard.KeyPressed += OnVirtualKeyPressed;
                _virtualKeyboard.BackspacePressed += OnVirtualKeyboardBackspace;
                _virtualKeyboard.EnterPressed += OnVirtualKeyboardEnter;
            }
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (DataContext is NetflixSpikeViewModel vm)
            {
                vm.LogEntries.CollectionChanged += (s, args) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _logScrollViewer?.ScrollToEnd();
                    });
                };

                vm.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(NetflixSpikeViewModel.IsMfaRequired) && vm.IsMfaRequired)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            var pin1 = this.FindControl<Button>("PinKey1");
                            pin1?.Focus();
                        }, DispatcherPriority.Input);
                    }
                };
            }
        }

        private void OnVirtualKeyPressed(string key)
        {
            if (DataContext is NetflixSpikeViewModel vm)
            {
                vm.AppendMfaPin(key);
            }
        }

        private void OnVirtualKeyboardBackspace()
        {
            if (DataContext is NetflixSpikeViewModel vm)
            {
                vm.BackspaceMfaPin();
            }
        }

        private void OnVirtualKeyboardEnter()
        {
            if (DataContext is NetflixSpikeViewModel vm)
            {
                vm.SubmitMfaCommand.Execute().Subscribe();
            }
        }

        private void OnControlKeyDown(object? sender, KeyEventArgs e)
        {
            if (DataContext is not NetflixSpikeViewModel vm) return;

            if (vm.IsMfaRequired)
            {
                if (e.Key >= Key.D0 && e.Key <= Key.D9)
                {
                    vm.AppendMfaPin(((int)e.Key - (int)Key.D0).ToString());
                    e.Handled = true;
                }
                else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
                {
                    vm.AppendMfaPin(((int)e.Key - (int)Key.NumPad0).ToString());
                    e.Handled = true;
                }
                else if (e.Key >= Key.A && e.Key <= Key.Z)
                {
                    vm.AppendMfaPin(e.Key.ToString());
                    e.Handled = true;
                }
                else if (e.Key == Key.Back)
                {
                    vm.BackspaceMfaPin();
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter)
                {
                    vm.SubmitMfaCommand.Execute().Subscribe();
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    vm.StopCommand.Execute().Subscribe();
                    e.Handled = true;
                }
            }
        }
    }
}
