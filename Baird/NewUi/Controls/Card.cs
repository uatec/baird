using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Baird.NewUi.Model;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Controls
{
    public enum CardShape { Wide, Poster }

    /// <summary>
    /// A card: artwork (or a typographic fallback, never a grey box), badges, progress, and a label underneath.
    /// Lit = lifted ×1.12 with a bright ring and soft glow; Dimmed = a neighbour of the lit card.
    /// </summary>
    public sealed class Card : StackPanel
    {
        public static Size SizeOf(CardShape s) => s == CardShape.Wide ? new Size(384, 216) : new Size(216, 324);

        public HomeCard Data { get; }
        private readonly Border _art;
        private readonly Border _ring;
        private readonly Border _dim;
        private readonly StackPanel _text;
        private bool _lit;

        public Card(HomeCard data, CardShape shape, Size? customSize = null)
        {
            Data = data;
            var size = customSize ?? SizeOf(shape);
            Spacing = 16;
            Width = size.Width;
            RenderTransformOrigin = new RelativePoint(0.5, 0.4, RelativeUnit.Relative);
            Motion.Animate(this, T.Fast);

            var layers = new Grid();
            var isLogo = data.Kind == CardKind.Channel && !string.IsNullOrEmpty(data.ImageUrl);
            var fallback = (Border)Fallback(data, shape, showTitle: !isLogo);
            layers.Children.Add(fallback);
            // What's on, when the guide has a picture of it: fills the card behind the channel's logo.
            if (data.Kind == CardKind.Channel && data.Programme?.ImageUrl is { Length: > 0 } still)
            {
                var art = new Image { Stretch = Stretch.UniformToFill, Opacity = 0, VerticalAlignment = VerticalAlignment.Top };
                Motion.Animate(art, T.Slow);
                art.PropertyChanged += (_, e) => { if (e.Property == Image.SourceProperty && art.Source != null) art.Opacity = 1; };
                AsyncImageLoader.ImageLoader.SetSource(art, still);
                layers.Children.Add(art);
                layers.Children.Add(new Border { Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#99000000"), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) } } });
            }
            var hasStill = data.Kind == CardKind.Channel && data.Programme?.ImageUrl is { Length: > 0 };
            if (!string.IsNullOrEmpty(data.ImageUrl))
            {
                // A channel's image is its logo: shown whole, smallish, above the name; artwork fills the card.
                var img = isLogo && hasStill
                    // Over a programme picture the logo is a small badge, top right.
                    ? new Image { Stretch = Stretch.Uniform, Opacity = 0, MaxHeight = 44, MaxWidth = 110, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 16, 16, 0) }
                    : isLogo
                    ? new Image { Stretch = Stretch.Uniform, Opacity = 0, MaxHeight = 92, MaxWidth = 220, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 34, 0, 0) }
                    : new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
                Motion.Animate(img, T.Slow);
                var tintFromLogo = isLogo && !hasStill;
                img.PropertyChanged += (_, e) =>
                {
                    if (e.Property != Image.SourceProperty || img.Source == null) return;
                    img.Opacity = 1;
                    // Last step of the artwork fallback: no picture of what's on, so the card takes the channel's colours.
                    if (tintFromLogo && img.Source is Avalonia.Media.Imaging.Bitmap logo && BrandColour.Of(logo) is { } brand)
                        fallback.Background = BrandGradient(brand);
                };
                AsyncImageLoader.ImageLoader.SetSource(img, data.ImageUrl);
                layers.Children.Add(img);
            }
            if (data.Kind == CardKind.Channel)
            {
                layers.Children.Add(new Border { Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 0.45, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#C0000000"), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) } } });
                layers.Children.Add(new TextBlock { Text = data.Label, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = T.Text, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16, 0, 16, 30) });
            }
            if (data.Badge == "live") layers.Children.Add(Pill("● Live", T.Live, T.Text, HorizontalAlignment.Left));
            if (data.Badge == "get") layers.Children.Add(Pill("⤓ Request", T.FocusFill, T.FocusInk, HorizontalAlignment.Right));
            if (data.Badge == "requested") layers.Children.Add(Pill("Requested", T.Pill, T.Text, HorizontalAlignment.Right));
            if (data.Badge == "partial") layers.Children.Add(Pill("Partly available", T.Pill, T.Text, HorizontalAlignment.Right));
            if (data.Badge == "missing") layers.Children.Add(Pill("Not streaming", T.Pill, T.Text, HorizontalAlignment.Left));
            // Downloading: the percentage, like a film's (episodes too).
            if (data.Badge is { } b && b.StartsWith("dl:", StringComparison.Ordinal)) layers.Children.Add(Pill($"⤓ Downloading {b[3..]}%", T.Downloading, T.FocusInk, HorizontalAlignment.Left));
            if (data.Progress is { } p and > 0)
            {
                var track = new Grid { Height = 5, Margin = new Thickness(14, 0, 14, 12), VerticalAlignment = VerticalAlignment.Bottom, ClipToBounds = true };
                track.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#4DFFFFFF")), CornerRadius = new CornerRadius(3) });
                track.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = (size.Width - 28) * Math.Clamp(p, 0, 1) });
                layers.Children.Add(track);
            }
            _dim = new Border { Background = Brushes.Black, Opacity = 0 };
            Motion.Animate(_dim, T.Fast);
            layers.Children.Add(_dim);

            _art = new Border { Width = size.Width, Height = size.Height, CornerRadius = new CornerRadius(T.RCard), ClipToBounds = true, Background = T.CardBack, BoxShadow = T.CardShadow, Child = layers };
            _ring = new Border { Width = size.Width + 8, Height = size.Height + 8, Margin = new Thickness(-4), CornerRadius = new CornerRadius(T.RCard + 4), BorderBrush = Brushes.White, BorderThickness = new Thickness(4), BoxShadow = T.FocusGlow, Opacity = 0, IsHitTestVisible = false };
            Motion.Animate(_ring, T.Fast);
            var artStack = new Grid();
            artStack.Children.Add(_art);
            artStack.Children.Add(_ring);
            Children.Add(artStack);

            var label = data.Kind == CardKind.Channel ? data.Sublabel ?? "No programme information" : data.Label;
            var sub = data.Kind == CardKind.Channel ? null : data.Sublabel;
            _text = new StackPanel { Opacity = 0.55, Spacing = 4 };
            _text.Children.Add(new TextBlock { Text = label, FontSize = 24, FontWeight = FontWeight.SemiBold, Foreground = T.Text, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(sub)) _text.Children.Add(new TextBlock { Text = sub, FontSize = 21, Foreground = T.Text2, TextTrimming = TextTrimming.CharacterEllipsis });
            Motion.Animate(_text, T.Fast);
            Children.Add(_text);
        }

        public bool Lit
        {
            get => _lit;
            set
            {
                _lit = value;
                Classes.Set("focused", value);
                RenderTransform = value ? Motion.Scale(T.FocusLift) : Motion.None;
                ZIndex = value ? 2 : 0;
                _ring.Opacity = value ? 1 : 0;
                _text.Opacity = value ? 1 : 0.55;
                if (value) _dim.Opacity = 0;
            }
        }

        /// <summary>A neighbour of the lit card dims, so the lit one is unmistakable.</summary>
        public bool Dimmed { set => _dim.Opacity = value && !_lit ? 0.45 : 0; }

        private static Border Pill(string text, IBrush back, IBrush fore, HorizontalAlignment h) => new()
        {
            Background = back,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(12, 4),
            Margin = new Thickness(12),
            HorizontalAlignment = h,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = text, FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = fore },
        };

        /// <summary>No artwork: a tinted card with the title set large (the agreed fallback).</summary>
        /// <summary>A rich gradient in a channel's brand colour (never plain black).</summary>
        public static IBrush BrandGradient(Color brand)
        {
            var hsv = brand.ToHsv();
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.85, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.15, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(HsvColor.FromAhsv(1, hsv.H, 0.7, 0.5).ToRgb(), 0),
                    new GradientStop(HsvColor.FromAhsv(1, (hsv.H + 20) % 360, 0.75, 0.22).ToRgb(), 0.6),
                    new GradientStop(HsvColor.FromAhsv(1, (hsv.H + 30) % 360, 0.8, 0.1).ToRgb(), 1),
                },
            };
        }

        private static Control Fallback(HomeCard d, CardShape shape, bool showTitle)
        {
            var hue = 0; foreach (var c in d.Id) hue = (hue * 31 + c) % 360;
            var top = HsvColor.FromAhsv(1, hue, 0.55, 0.36).ToRgb();
            var bottom = HsvColor.FromAhsv(1, hue, 0.6, 0.1).ToRgb();
            var title = d.Kind == CardKind.Channel ? d.Sublabel ?? "" : d.Label;
            return new Border
            {
                Background = new LinearGradientBrush { StartPoint = new RelativePoint(0.8, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.2, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) } },
                Child = !showTitle ? null : new TextBlock { Text = title, FontSize = shape == CardShape.Wide ? 30 : 26, FontWeight = FontWeight.Bold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = d.Kind == CardKind.Channel ? VerticalAlignment.Center : VerticalAlignment.Bottom, Margin = new Thickness(20) },
            };
        }
    }
}
