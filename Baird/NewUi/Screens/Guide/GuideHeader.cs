using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Baird.NewUi.Model;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// The guide's top: the dark ground (with a hole the playing video shows through as the preview), an ambient tint
    /// for the lit channel, the description of the lit programme (crossfades, only when the programme changes, so it
    /// never flickers), the preview's label, the time-jump strip and the typed-number pill.
    /// </summary>
    public sealed class GuideHeader : Panel
    {
        /// <summary>Where the preview sits, in stage coordinates (prototype: right 96, top 130, 520×292).</summary>
        public static readonly Rect PreviewRect = new(1920 - T.SafeX - 520, 130, 520, 292);
        private const double PreviewRadius = 18;

        private readonly Avalonia.Controls.Shapes.Path _ground = new() { Fill = new SolidColorBrush(Color.Parse("#F4050507")) };
        private readonly Panel _ambient = new() { Opacity = 0.45 };
        private readonly Panel _scrimHost = new();
        private readonly Panel _info = new() { Width = 1060, Height = 330, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(T.SafeX, 110, 0, 0), ClipToBounds = false };
        private readonly Panel _preview = new() { Width = PreviewRect.Width, Height = PreviewRect.Height, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(PreviewRect.X, PreviewRect.Y, 0, 0) };
        private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(T.SafeX - 22, 448, 0, 0) };
        private readonly Border _entry;
        private readonly TextBlock _entryText = new() { FontSize = 30, FontWeight = FontWeight.Bold, Foreground = T.Text, LetterSpacing = 2 };
        private string? _shownInfo, _shownPreview, _shownAmbient;
        private bool? _hole;

        public GuideHeader()
        {
            Width = 1920; Height = 1080;
            IsHitTestVisible = false;
            _scrimHost.Children.Add(_ambient);
            _scrimHost.Children.Add(new Border
            {
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#4D000000"), 0), new GradientStop(Color.Parse("#BF000000"), 0.42), new GradientStop(Color.Parse("#EB000000"), 0.6) },
                },
            });
            _entry = new Border
            {
                Background = T.Glass,
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(24, 10),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 440, T.SafeX, 0),
                Opacity = 0,
                Child = _entryText,
            };
            Motion.Animate(_entry, T.Fast);
            Children.Add(_ground);
            Children.Add(_scrimHost);
            Children.Add(_info);
            Children.Add(_preview);
            Children.Add(_strip);
            Children.Add(_entry);
            SetHole(false);
        }

        public string EntryText => _entryText.Text ?? "";

        /// <summary>Cut the preview hole (the video, framed by the shell, shows through) or fill it.</summary>
        public void SetHole(bool hole)
        {
            if (_hole == hole) return;
            _hole = hole;
            var full = new RectangleGeometry(new Rect(0, 0, 1920, 1080));
            Geometry shape = hole ? new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(PreviewRect, PreviewRadius, PreviewRadius)) : full;
            _ground.Data = shape;
            _scrimHost.Clip = hole ? shape : null;
            _shownPreview = null;
        }

        public void ShowEntry(string text, bool on) { if (on) _entryText.Text = text; _entry.Opacity = on ? 1 : 0; }

        public void SetStrip(IReadOnlyList<string> stops, int current)
        {
            if (_strip.Children.Count != stops.Count + 1)
            {
                _strip.Children.Clear();
                foreach (var s in stops) _strip.Children.Add(new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(22, 8), Child = new TextBlock { Text = s, FontSize = 23, FontWeight = FontWeight.SemiBold } });
                _strip.Children.Add(new TextBlock { Text = "⏮ ⏭  jump", FontSize = 21, Foreground = T.Text3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) });
            }
            for (var i = 0; i < stops.Count; i++)
            {
                var b = (Border)_strip.Children[i];
                b.Background = i == current ? T.Pill : Brushes.Transparent;
                ((TextBlock)b.Child!).Foreground = i == current ? T.Text : T.Text2;
            }
        }

        /// <summary>Describe the lit programme. Crossfades only when it changes (ids), so moving never flickers the header.</summary>
        public void Describe(Channel ch, Programme p, DateTimeOffset now, IReadOnlyList<(string Key, string Label)> keys)
        {
            var id = $"{ch.Id}|{p.Id}|{p.IsOnAt(now)}";
            if (_shownInfo == id) return;
            _shownInfo = id;

            var eyebrow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            eyebrow.Children.Add(new TextBlock { Text = $"{ch.Number}  {ch.Name}", FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = T.Text, VerticalAlignment = VerticalAlignment.Center });
            if (!p.NoInfo) eyebrow.Children.Add(new TextBlock { Text = $"{DayWord(p.Start, now)} {Hhmm(p.Start)}–{Hhmm(p.End)}", FontSize = 26, Foreground = T.Text2, VerticalAlignment = VerticalAlignment.Center });
            if (p.IsOnAt(now) || p.NoInfo) eyebrow.Children.Add(Pill("Live", T.Live));
            var layer = new StackPanel { Spacing = 0 };
            layer.Children.Add(eyebrow);
            layer.Children.Add(new TextBlock { Text = p.NoInfo ? ch.Name : p.Title, FontSize = 72, FontWeight = FontWeight.Bold, LetterSpacing = -1.5, Foreground = T.Text, Margin = new Thickness(0, 18, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 1060 });
            if (p.Subtitle != null) layer.Children.Add(new TextBlock { Text = p.Subtitle, FontSize = 28, Foreground = T.Text2, Margin = new Thickness(0, 0, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis });
            layer.Children.Add(new TextBlock { Text = p.NoInfo ? "No information for this channel." : p.Synopsis ?? "", FontSize = 26, LineHeight = 36, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 980, Margin = new Thickness(0, 10, 0, 0) });
            var hints = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 30, Margin = new Thickness(0, 22, 0, 0) };
            foreach (var (key, label) in keys) hints.Children.Add(Hint(key, label));
            layer.Children.Add(hints);
            Crossfade(_info, layer, 10);

            // The ambient light: the channel's hue, softly, behind the header (fades only when the channel changes).
            if (_shownAmbient == ch.Id) return;
            _shownAmbient = ch.Id;
            var hue = Hue(ch.Id);
            var amb = new Border
            {
                Background = new RadialGradientBrush
                {
                    Center = new RelativePoint(0.7, 0.3, RelativeUnit.Relative),
                    GradientOrigin = new RelativePoint(0.7, 0.3, RelativeUnit.Relative),
                    RadiusX = new RelativeScalar(0.7, RelativeUnit.Relative),
                    RadiusY = new RelativeScalar(0.8, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(HsvColor.FromAhsv(1, hue, 0.6, 0.45).ToRgb(), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.7) },
                },
            };
            Crossfade(_ambient, amb, 0, T.Slow);
        }

        /// <summary>The preview's label (and, with nothing playing, a typographic tile for the lit channel).</summary>
        public void Preview(string label, bool live, bool playing, Channel ch, Programme? onNow)
        {
            var id = $"{playing}|{label}|{(playing ? "" : ch.Id + onNow?.Id)}";
            if (_shownPreview == id) return;
            _shownPreview = id;
            var layer = new Panel();
            if (!playing)
            {
                var hue = Hue(ch.Id);
                layer.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(PreviewRadius),
                    ClipToBounds = true,
                    Background = new LinearGradientBrush { StartPoint = new RelativePoint(0.8, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.2, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(HsvColor.FromAhsv(1, hue, 0.55, 0.36).ToRgb(), 0), new GradientStop(HsvColor.FromAhsv(1, hue, 0.6, 0.1).ToRgb(), 1) } },
                    BoxShadow = BoxShadows.Parse("0 30 70 0 #B3000000"),
                    Child = new TextBlock { Text = onNow is { NoInfo: false } ? onNow.Title : ch.Name, FontSize = 34, FontWeight = FontWeight.Bold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap, MaxLines = 2, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(24) },
                });
            }
            var tag = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            tag.Children.Add(Pill(playing ? live ? "Live" : "Playing" : "Preview", playing && live ? T.Live : T.Pill));
            tag.Children.Add(new TextBlock { Text = label, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = T.Text, VerticalAlignment = VerticalAlignment.Center });
            layer.Children.Add(tag);
            Crossfade(_preview, layer, 0);
        }

        private static void Crossfade(Panel host, Control layer, double dy, TimeSpan? duration = null)
        {
            var d = duration ?? (Motion.Lite ? T.Fast : TimeSpan.FromMilliseconds(320));
            foreach (var old in host.Children.ToList())
            {
                Motion.Animate(old, d);
                old.Opacity = 0;
                DispatcherTimer.RunOnce(() => host.Children.Remove(old), d + TimeSpan.FromMilliseconds(40));
            }
            layer.Opacity = 0;
            layer.RenderTransform = TransformOperations.Parse($"translate(0px, {dy}px)");
            host.Children.Add(layer);
            DispatcherTimer.RunOnce(() => { Motion.Animate(layer, d); layer.Opacity = 1; layer.RenderTransform = Motion.None; }, TimeSpan.FromMilliseconds(16));
        }

        private static Border Pill(string text, IBrush back) => new()
        {
            Background = back,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(14, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 20, FontWeight = FontWeight.Bold, Foreground = T.Text },
        };

        private static Control Hint(string key, string label) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new Border { BorderBrush = T.Text3, BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 2), Child = new TextBlock { Text = key, FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = T.Text2 } },
                new TextBlock { Text = label, FontSize = 22, Foreground = T.Text2, VerticalAlignment = VerticalAlignment.Center },
            },
        };

        public static string Hhmm(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm");

        public static string DayWord(DateTimeOffset t, DateTimeOffset now)
        {
            var days = (t.ToLocalTime().Date - now.ToLocalTime().Date).Days;
            return days switch { 0 => "Today", 1 => "Tomorrow", -1 => "Yesterday", _ => t.ToLocalTime().ToString("dddd") };
        }

        private static int Hue(string id) { var h = 0; foreach (var c in id) h = (h * 31 + c) % 360; return h; }
    }
}
