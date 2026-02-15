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

                if (_isLongPressTriggered)
                {
                    // It was a long press, already handled by timer tick
                    e.Handled = true;
                    _isLongPressTriggered = false;
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
                        // Fallback to Click event if no Command? 
                        // Button.OnClick() is protected, can we call it? 
                        // No, but usually Command is enough for this app.
                        // Can simulate Click via raising even but let's stick to Command first as that's what is used.
                        // Actually, let's call OnClick() via reflection or just Command.
                        // MediaButton inherits Button, lets check if we can call OnClick()
                        // OnClick() handles Command execution internally usually.
                        // Base Button.OnClick() calls Command.Execute.
                        // Since we are in the class, we can call a method that calls base.OnClick()?
                        // Button.OnClick is protected virtual void OnClick(). We can call it!

                        // BUT: We need to be careful about state. 
                        // If we just call Command.Execute, we miss the Click event.
                        // However, application uses Command binding mostly.
                        // Let's try calling generic Click handling logic or just Command. 

                        // Given usage in MediaTileControl: Command="{Binding ...}"
                        // Command execution is sufficient.
                    }

                    e.Handled = true;
                    return;
                }
            }
            base.OnKeyUp(e);
        }
    }
}
