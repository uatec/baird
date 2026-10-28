using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Baird.NewUi.Lifecycle;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// "Still watching?": shown over a paused stream after nearly three idle hours. It takes no keys itself: TvLifecycle treats
    /// any key as the answer (the stream resumes), and starts the screensaver if five minutes pass without one.
    /// </summary>
    public sealed class StillWatchingPrompt : IStillWatching
    {
        private readonly Panel _root = new() { Width = 1920, Height = 1080, IsHitTestVisible = false, IsVisible = false };

        public Control View => _root;
        public bool IsShowing => _root.IsVisible;
        /// <summary>Everything the prompt says (tests).</summary>
        public string Text => string.Join("\n", ((StackPanel)((Border)_root.Children[1]).Child!).Children.OfType<TextBlock>().Select(t => t.Text));

        public StillWatchingPrompt()
        {
            _root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)) });
            _root.Children.Add(new Border
            {
                Width = 820,
                Padding = new Thickness(48, 40),
                CornerRadius = new CornerRadius(26),
                Background = T.Glass,
                BoxShadow = BoxShadows.Parse("0 30 80 0 #CC000000"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new StackPanel
                {
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock { Text = "Still watching?", FontSize = 56, FontWeight = FontWeight.Bold, Foreground = T.Text, LetterSpacing = -1 },
                        new TextBlock { Text = "Press any button to keep watching.", FontSize = 28, Foreground = T.Text2 },
                        new TextBlock { Text = "Otherwise the screensaver starts in five minutes.", FontSize = 22, Foreground = T.Text2 },
                    },
                },
            });
        }

        public void Show() => _root.IsVisible = true;
        public void Hide() => _root.IsVisible = false;
    }
}
