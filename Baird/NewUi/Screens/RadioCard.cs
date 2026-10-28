using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Baird.NewUi.Controls;
using Baird.NewUi.Model;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// What fills the screen for a channel with sound but no picture (radio): its logo on its brand colours, the
    /// channel's number and name, and what's on with its time and progress. Sits over the (empty) video, under every screen.
    /// </summary>
    public sealed class RadioCard : Panel
    {
        private readonly Border _logoBox = new() { Width = 360, Height = 360, CornerRadius = new CornerRadius(T.RPanel), ClipToBounds = true, Background = T.CardBack, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _channel = new() { FontSize = T.H3, FontWeight = FontWeight.SemiBold, Foreground = T.Text2 };
        private readonly TextBlock _title = new() { FontSize = T.Title, FontWeight = FontWeight.Bold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
        private readonly TextBlock _times = new() { FontSize = T.Body, Foreground = T.Text2 };
        private readonly TextBlock _synopsis = new() { FontSize = T.Body, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap, MaxLines = 4, MaxWidth = 980 };
        private readonly Border _fill = new() { Height = 6, CornerRadius = new CornerRadius(3), Background = T.Text, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly Border _bar = new() { Height = 6, CornerRadius = new CornerRadius(3), Background = T.Pill, Width = 980, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly Border _back = new() { Background = Brushes.Black };
        private readonly StackPanel _row, _text;
        private string? _logoFor;

        public RadioCard()
        {
            IsHitTestVisible = false;
            IsVisible = false;
            _bar.Child = _fill;
            _text = new StackPanel
            {
                Spacing = 14, VerticalAlignment = VerticalAlignment.Center,
                Children = { _channel, _title, _times, new Border { Height = 4 }, _bar, new Border { Height = 8 }, _synopsis },
            };
            Children.Add(_back);
            Children.Add(_row = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 72,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(T.SafeX + 40, 0, T.SafeX, 0),
                Children = { _logoBox, _text },
            });
        }

        /// <summary>Scaled into the guide's small preview frame: just a big logo, centred (the guide names the channel itself).</summary>
        public void SetCompact(bool compact)
        {
            _text.IsVisible = !compact;
            _logoBox.Width = compact ? 1300 : 360;
            _logoBox.Height = compact ? 680 : 360;
            _row.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            _row.Margin = compact ? new Thickness(0) : new Thickness(T.SafeX + 40, 0, T.SafeX, 0);
        }

        /// <summary>Shown while a radio channel plays; null hides it.</summary>
        public void Update(Channel? channel, Programme? now, DateTimeOffset at)
        {
            IsVisible = channel != null;
            if (channel == null) return;
            _channel.Text = $"{channel.Number}  ·  {channel.Name}";
            var real = now is { NoInfo: false };
            _title.Text = real ? now!.Title : channel.Name;
            _times.Text = real ? $"{now!.Start.LocalDateTime:HH:mm} – {now.End.LocalDateTime:HH:mm}" : "No programme information";
            _synopsis.Text = real ? now!.Synopsis ?? now.Subtitle ?? "" : "";
            _synopsis.IsVisible = _synopsis.Text.Length > 0;
            _bar.IsVisible = real;
            _fill.Width = real ? _bar.Width * now!.ProgressAt(at) : 0;
            if (_logoFor != channel.Id) ShowLogo(channel);
        }

        private void ShowLogo(Channel ch)
        {
            _logoFor = ch.Id;
            var hue = 0; foreach (var c in ch.Id) hue = (hue * 31 + c) % 360;
            _logoBox.Background = Card.BrandGradient(HsvColor.FromAhsv(1, hue, 0.6, 0.5).ToRgb());
            if (ch.LogoUrl is not { Length: > 0 } url)
            {
                _logoBox.Child = new TextBlock { Text = ch.Name, FontSize = T.H2, FontWeight = FontWeight.Bold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28), VerticalAlignment = VerticalAlignment.Bottom };
                return;
            }
            var logo = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(48), Opacity = 0 };
            logo.PropertyChanged += (_, e) =>
            {
                if (e.Property != Image.SourceProperty || logo.Source == null || _logoFor != ch.Id) return;
                logo.Opacity = 1;
                if (logo.Source is Avalonia.Media.Imaging.Bitmap bmp && BrandColour.Of(bmp) is { } brand) _logoBox.Background = Card.BrandGradient(brand);
            };
            AsyncImageLoader.ImageLoader.SetSource(logo, url);
            _logoBox.Child = logo;
        }
    }
}
