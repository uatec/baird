using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace Baird.Tests.Controls
{
    /// <summary>
    /// Remote-only mode: a hidden pointer must not leave hover states on the controls it passes over. Hover comes from
    /// hit-testing, which runs before any event handler, so the content has to be invisible to hit-testing itself.
    /// </summary>
    public class PointerIsolationTests
    {
        private static readonly Point Centre = new(200, 150);

        private static (Window Window, Border Target) Open(bool hitTestable)
        {
            var target = new Border { Background = Brushes.Red, Width = 200, Height = 100 };
            var content = new Border { Child = target, IsHitTestVisible = hitTestable };
            var window = new Window { Width = 400, Height = 300, Content = content };
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            return (window, target);
        }

        [AvaloniaFact]
        public void Content_that_is_hit_testable_is_hit_under_the_pointer()
        {
            var (window, target) = Open(hitTestable: true);
            Assert.Same(target, window.InputHitTest(Centre));
        }

        [AvaloniaFact]
        public void Content_that_is_not_hit_testable_is_not_hit_but_the_window_still_sees_the_click()
        {
            var (window, target) = Open(hitTestable: false);
            var presses = 0;
            window.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs _) => presses++, RoutingStrategies.Tunnel);

            window.MouseMove(Centre);
            window.MouseDown(Centre, MouseButton.Left);
            window.MouseUp(Centre, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.NotSame(target, window.InputHitTest(Centre));
            Assert.False(target.IsPointerOver);
            Assert.Equal(1, presses);
        }
    }
}
