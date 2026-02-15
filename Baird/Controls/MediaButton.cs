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
                if (!_longPressTimer!.IsEnabled && !_isLongPressTriggered)
                {
                    _isLongPressTriggered = false;
                    _longPressTimer.Start();
                }
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _longPressTimer?.Stop();
                if (_isLongPressTriggered)
                {
                    e.Handled = true; // Prevent Click
                    _isLongPressTriggered = false;
                    return;
                }
            }
            base.OnKeyUp(e);
        }
    }
}
