using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Baird.NewUi.Controls;
using Baird.NewUi.Core;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// Settings › Connect a phone: two codes. The first (any camera) opens Baird's setup page, which adds the "Play on Baird"
    /// Shortcut; the second is scanned by the Shortcut itself the first time it runs, and holds Baird's address and a new token.
    /// </summary>
    public sealed class PhonesPage : IScreen
    {
        private readonly Panel _root = new() { Width = 1920, Height = 1080, Background = new SolidColorBrush(Color.Parse("#F0000000")) };
        public Control View => _root;

        /// <summary>The second code's text: {"baird": base URL, "token": …} (what the Shortcut saves and reads).</summary>
        public static string PairingText(string baseUrl, string token)
        {
            using var stream = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(stream))
            {
                w.WriteStartObject();
                w.WriteString("baird", baseUrl);
                w.WriteString("token", token);
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        /// <param name="baseUrl">Where phones reach Baird (null: not on a home network, so nothing to scan).</param>
        /// <param name="issueToken">A new token for this screen's pairing code.</param>
        public PhonesPage(string? baseUrl, System.Func<string> issueToken)
        {
            var content = new StackPanel { Margin = new Thickness(T.SafeX, 110, T.SafeX, 0), Spacing = 18, HorizontalAlignment = HorizontalAlignment.Left };
            content.Children.Add(new TextBlock { Text = "Connect a phone", FontSize = T.Title, FontWeight = FontWeight.Bold, Foreground = T.Text });
            content.Children.Add(new TextBlock
            {
                Text = "Then share a video from YouTube, iPlayer, Netflix, Safari… to “Play on Baird”, and it plays here. iPhone only, on this home Wi-Fi.",
                FontSize = T.Body,
                Foreground = T.Text2,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 1500,
            });
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
            }
            else
            {
                var codes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 140, Margin = new Thickness(0, 50, 0, 0) };
                codes.Children.Add(Step("1", "Add the Shortcut", "Scan with the phone's camera, then tap Add Shortcut.", baseUrl + "/"));
                codes.Children.Add(Step("2", "Connect it", "Share a video to Play on Baird. The first time, it asks to scan: point it here.", PairingText(baseUrl, issueToken())));
                content.Children.Add(codes);
                content.Children.Add(new TextBlock { Text = baseUrl, FontSize = T.Small, Foreground = T.Text3, Margin = new Thickness(0, 30, 0, 0) });
            }
            _root.Children.Add(content);
        }

        private static Control Step(string number, string title, string detail, string code) => new StackPanel
        {
            Width = 640,
            Spacing = 14,
            Children =
            {
                new Border { Width = 420, Height = 420, CornerRadius = new CornerRadius(T.RPanel), ClipToBounds = true, Background = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left, Child = new QrCode(code) { Width = 420, Height = 420 } },
                new TextBlock { Text = $"{number}   {title}", FontSize = T.H2, FontWeight = FontWeight.SemiBold, Foreground = T.Text, Margin = new Thickness(0, 16, 0, 0) },
                new TextBlock { Text = detail, FontSize = T.Body, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap },
            },
        };

        /// <summary>Nothing to move between; Back leaves (the shell pops the page).</summary>
        public bool OnAction(UiAction a, bool repeat) => a != UiAction.Back;
    }
}
