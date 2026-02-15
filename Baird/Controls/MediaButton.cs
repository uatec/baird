using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Windows.Input;

namespace Baird.Controls
{
    public class MediaButton : Button
    {
        public static readonly StyledProperty<ICommand> LongPressCommandProperty =
            AvaloniaProperty.Register<MediaButton, ICommand>(nameof(LongPressCommand));

        public ICommand LongPressCommand
        {
            get => GetValue(LongPressCommandProperty);
            set => SetValue(LongPressCommandProperty, value);
        }

        public static readonly StyledProperty<object> LongPressCommandParameterProperty =
            AvaloniaProperty.Register<MediaButton, object>(nameof(LongPressCommandParameter));

        public object LongPressCommandParameter
        {
            get => GetValue(LongPressCommandParameterProperty);
            set => SetValue(LongPressCommandParameterProperty, value);
        }

        private DispatcherTimer? _longPressTimer;
        private bool _isLongPressTriggered;
        private TimeSpan _longPressDuration = TimeSpan.FromMilliseconds(800);
        private bool _isKeyDownCaptured; // Prevent "ghost clicks" from navigation events

        protected override Type StyleKeyOverride => typeof(Button);

        public MediaButton()
        {
            _longPressTimer = new DispatcherTimer
            {
                Interval = _longPressDuration
            };
            _longPressTimer.Tick += OnLongPressTimerTick;
        }

        private void OnLongPressTimerTick(object? sender, EventArgs e)
        {
            _longPressTimer?.Stop();
            _isLongPressTriggered = true;

            if (LongPressCommand?.CanExecute(LongPressCommandParameter) == true)
            {
                LongPressCommand.Execute(LongPressCommandParameter);
                // Visual feedback could be added here
                Console.WriteLine("[MediaButton] Long Press Triggered");
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _isLongPressTriggered = false;
                _longPressTimer?.Stop();
                _longPressTimer?.Start();
            }
            base.OnPointerPressed(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            _longPressTimer?.Stop();
            if (_isLongPressTriggered)
            {
                e.Handled = true; // Prevent Click
                _isLongPressTriggered = false;
                return;
            }
            base.OnPointerReleased(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                // Capture the key down event so we know we initiated this action
                _isKeyDownCaptured = true;

                // Start timer if not running and not already triggered
                if (!_longPressTimer!.IsEnabled && !_isLongPressTriggered)
                {
                    _isLongPressTriggered = false;
                    _longPressTimer.Start();
                }

                // CRITICIAL: Prevent base Button from handling Enter and triggering Click immediately
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _longPressTimer?.Stop();

                // Only handle if we captured the KeyDown (prevents executing when navigating TO this button with Enter held/released)
                if (!_isKeyDownCaptured)
                {
                    e.Handled = true;
                    return;
                }

                if (_isLongPressTriggered)
                {
                    // It was a long press, already handled by timer tick
                    e.Handled = true;
                    _isLongPressTriggered = false;
                    _isKeyDownCaptured = false;
                    return;
                }
                else
                {
                    // Short press - manually invoke Command
                    // We must handle this because we suppressed OnKeyDown
                    if (Command?.CanExecute(CommandParameter) == true)
                    {
                        Command.Execute(CommandParameter);
                    }
                    else
                    {
                        // Fallback to Click event if needed
                    }

                    e.Handled = true;
                    _isKeyDownCaptured = false;
                    return;
                }
            }
            base.OnKeyUp(e);
        }

        protected override void OnLostFocus(RoutedEventArgs e)
        {
            base.OnLostFocus(e);
            _isKeyDownCaptured = false;
            _isLongPressTriggered = false;
            _longPressTimer?.Stop();
        }
    }
}
