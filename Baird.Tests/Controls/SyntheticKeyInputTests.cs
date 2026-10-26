using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Baird.Controls;
using ReactiveUI;
using Xunit;

namespace Baird.Tests.Controls
{
    public class SyntheticKeyInputTests
    {
        private static (Window Window, Button Focused, UserControl Parent) Open()
        {
            var focused = new Button { Content = "Focus me" };
            var parent = new UserControl { Content = focused };
            var window = new Window { Width = 400, Height = 300, Content = parent };
            window.Show();
            focused.Focus();
            Dispatcher.UIThread.RunJobs();
            return (window, focused, parent);
        }

        [AvaloniaFact]
        public void KeyDown_TriggersAKeyBindingOnAnAncestorOfTheFocusedElement()
        {
            var (window, _, parent) = Open();
            var executed = 0;
            parent.KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.PageUp),
                Command = ReactiveCommand.Create(() => { executed++; }),
            });

            SyntheticKeyInput.Raise(window, Key.PageUp, down: true);

            Assert.Equal(1, executed);
        }

        [AvaloniaFact]
        public void KeyDownAndUp_ReachTheFocusedElement()
        {
            var (window, focused, _) = Open();
            var events = new System.Collections.Generic.List<string>();
            focused.AddHandler(InputElement.KeyDownEvent, (object? _, KeyEventArgs e) => events.Add($"down {e.Key}"), RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
            focused.AddHandler(InputElement.KeyUpEvent, (object? _, KeyEventArgs e) => events.Add($"up {e.Key}"), RoutingStrategies.Bubble);
            var tunnelSeen = false;
            window.AddHandler(InputElement.KeyDownEvent, (object? _, KeyEventArgs _) => tunnelSeen = true, RoutingStrategies.Tunnel);

            SyntheticKeyInput.Raise(window, Key.D5, down: true);
            SyntheticKeyInput.Raise(window, Key.D5, down: false);

            Assert.True(tunnelSeen);
            Assert.Contains("down D5", events);
            Assert.Contains("up D5", events);
        }
    }
}
