using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Baird.NewUi.Controls;
using Baird.NewUi.Core;
using Baird.NewUi.Theme;
using Baird.Services.Cast;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// Settings › Connect a phone: one code, scanned twice. The camera opens it (Baird's setup page, which adds the "Play on
    /// Baird" Shortcut); then the Shortcut scans the same code the first time it runs and keeps it: it is this phone's address
    /// on Baird, with a new token in it.
    /// </summary>
    public sealed class PhonesPage : IScreen
    {
        private readonly Panel _root = new() { Width = 1920, Height = 1080, Background = new SolidColorBrush(Color.Parse("#F0000000")) };
        public Control View => _root;

        /// <param name="baseUrl">Where phones reach Baird (null: not on a home network, so nothing to scan).</param>
        /// <param name="issueToken">A new token for this screen's code.</param>
        public PhonesPage(string? baseUrl, System.Func<string> issueToken)
        {
            var content = new StackPanel { Margin = new Thickness(T.SafeX, 110, T.SafeX, 0), Spacing = 18, HorizontalAlignment = HorizontalAlignment.Left };
            content.Children.Add(new TextBlock { Text = "Connect a phone", FontSize = T.Title, FontWeight = FontWeight.Bold, Foreground = T.Text });
            if (baseUrl == null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = "Baird isn't connected to a home network, so a phone can't reach it. Check the network, then open this again.",
                    FontSize = T.H3,
                    Foreground = T.Text,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 1500,
                    Margin = new Thickness(0, 60, 0, 0),
                });
                _root.Children.Add(content);
                return;
            }

            var steps = new StackPanel { Spacing = 34, VerticalAlignment = VerticalAlignment.Center, Width = 900 };
            steps.Children.Add(Step("1", "Scan this with the iPhone's camera", "Tap Add Shortcut on the page it opens."));
            steps.Children.Add(Step("2", "Share a video to “Play on Baird”", "From YouTube, iPlayer, Netflix, Safari…"));
            steps.Children.Add(Step("3", "The first time, scan this code again", "From then on, sharing plays it here."));
            steps.Children.Add(new TextBlock { Text = "iPhone only, on this home Wi-Fi.", FontSize = T.Body, Foreground = T.Text3 });
            content.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 110,
                Margin = new Thickness(0, 60, 0, 0),
                Children =
                {
                    new Border
                    {
                        Width = 560, Height = 560, CornerRadius = new CornerRadius(T.RPanel), ClipToBounds = true, Background = Brushes.White,
                        Child = new QrCode(CastApi.PhoneUrl(baseUrl, issueToken())) { Width = 560, Height = 560 },
                    },
                    steps,
                },
            });
            _root.Children.Add(content);
        }

        private static Control Step(string number, string title, string detail) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 28,
            Children =
            {
                new Border
                {
                    Width = 64, Height = 64, CornerRadius = new CornerRadius(32), Background = T.Pill, VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock { Text = number, FontSize = T.H3, FontWeight = FontWeight.Bold, Foreground = T.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                },
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = title, FontSize = T.H3, FontWeight = FontWeight.SemiBold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 800 },
                        new TextBlock { Text = detail, FontSize = T.Body, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap, MaxWidth = 800 },
                    },
                },
            },
        };

        /// <summary>Nothing to move between; Back leaves (the shell pops the page).</summary>
        public bool OnAction(UiAction a, bool repeat) => a != UiAction.Back;
    }
}
