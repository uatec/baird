using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Baird.NewUi.Controls;
using Baird.NewUi.Model;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>A stretch of a channel's next few hours, as a share of the timeline.</summary>
    public sealed record TimelineSegment(Programme Programme, double Share, bool IsNow);

    /// <summary>A channel's next ~3 hours, to scale (prototype channels.js timeline): a 5-minute bulletin looks like one.</summary>
    public static class ChannelTimeline
    {
        public static readonly TimeSpan Span = TimeSpan.FromHours(3);

        public static IReadOnlyList<TimelineSegment> Segments(IReadOnlyList<Programme> schedule, DateTimeOffset now, TimeSpan? span = null)
        {
            var s = span ?? Span;
            var end = now + s;
            var output = new List<TimelineSegment>();
            foreach (var p in schedule)
            {
                if (p.End <= now) continue;
                if (p.Start >= end || output.Count == 4) break;
                var a = p.Start > now ? p.Start : now;
                var b = p.End < end ? p.End : end;
                output.Add(new TimelineSegment(p, (b - a).TotalSeconds / s.TotalSeconds, output.Count == 0 && p.IsOnAt(now)));
                if (p.NoInfo) break; // nothing known beyond it
            }
            return output;
        }
    }

    /// <summary>
    /// One channel in the channel list: a thumbnail (the programme's picture, else the channel's colours and logo), number
    /// and name with the times, what's on, and the timeline. Built once; lighting it only recolours its text (the
    /// highlight behind it is one shared Border that slides).
    /// </summary>
    public sealed class ChannelRow : Grid
    {
        public const double Width0 = 604, Height0 = 100, TimelineWidth = 400;
        private static readonly IBrush InkSoft = new SolidColorBrush(Color.Parse("#9E0A0A0C"));
        private static readonly IBrush SegLater = new SolidColorBrush(Color.Parse("#47FFFFFF"));
        private static readonly IBrush SegLaterLit = new SolidColorBrush(Color.Parse("#400A0A0C"));
        private static readonly IBrush SegNone = new SolidColorBrush(Color.Parse("#26FFFFFF"));
        private static readonly IBrush SegNow = new SolidColorBrush(Color.Parse("#D9FFFFFF"));

        public Channel Channel { get; }
        public Programme? Now { get; }
        public Control Thumb { get; }
        public IReadOnlyList<TimelineSegment> Segments { get; }
        private readonly TextBlock _number, _name, _times, _title, _watching;
        private readonly List<(Border Bar, TextBlock? Label, TimelineSegment Seg)> _bars = new();
        private bool _lit;

        public ChannelRow(Channel ch, IReadOnlyList<Programme> schedule, DateTimeOffset now, bool watching)
        {
            Channel = ch;
            Width = Width0; Height = Height0;
            Now = schedule.Count > 0 && schedule[0].Start <= now ? Core.GuideFocus.At(schedule, now) : null;
            if (Now != null && !Now.IsOnAt(now)) Now = null;
            Segments = ChannelTimeline.Segments(schedule, now);
            ColumnDefinitions = new ColumnDefinitions("Auto,*");

            Thumb = BuildThumb(ch, Now);
            Thumb.Margin = new Thickness(16, 0, 20, 0);
            Children.Add(Thumb);

            _number = new TextBlock { Text = ch.Number, FontSize = 24, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 8, 0) };
            _name = new TextBlock { Text = ch.Name, FontSize = 24, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 };
            _times = new TextBlock { Text = Now is { NoInfo: false } p ? $"   {p.Start.ToLocalTime():HH:mm}–{p.End.ToLocalTime():HH:mm}" : "", FontSize = 19 };
            _watching = new TextBlock { Text = watching ? "  · watching" : "", FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = T.Live };
            _title = new TextBlock { Text = Now is { NoInfo: false } q ? q.Title : "No programme information", FontSize = 26, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420, Margin = new Thickness(0, 2, 0, 6) };

            var timeline = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Height = 22, ClipToBounds = true, Width = TimelineWidth, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var seg in Segments)
            {
                var w = Math.Max(4, seg.Share * (TimelineWidth - 3 * Math.Max(0, Segments.Count - 1)));
                var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
                var cell = new Panel { Width = w, Children = { bar } };
                TextBlock? label = null;
                if (!seg.IsNow && !seg.Programme.NoInfo)
                {
                    label = new TextBlock { Text = $"{seg.Programme.Start.ToLocalTime():HH:mm} {seg.Programme.Title}", FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = w, Margin = new Thickness(0, 9, 0, 0) };
                    cell.Children.Add(label);
                }
                _bars.Add((bar, label, seg));
                timeline.Children.Add(cell);
            }

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { _number, _name, _times, _watching } }, _title, timeline } };
            SetColumn(text, 1);
            Children.Add(text);
            Apply();
        }

        public bool Lit { get => _lit; set { if (_lit == value) return; _lit = value; Apply(); } }
        public bool Watching { set => _watching.Text = value ? "  · watching" : ""; }

        private void Apply()
        {
            _number.Foreground = _lit ? InkSoft : T.Text3;
            _name.Foreground = _lit ? InkSoft : T.Text2;
            _times.Foreground = _lit ? InkSoft : T.Text3;
            _title.Foreground = _lit ? T.FocusInk : T.Text;
            Thumb.Opacity = _lit ? 1 : 0.8;
            foreach (var (bar, label, seg) in _bars)
            {
                bar.Background = seg.Programme.NoInfo ? SegNone : seg.IsNow ? (_lit ? T.FocusInk : SegNow) : (_lit ? SegLaterLit : SegLater);
                if (label != null) label.Foreground = _lit ? InkSoft : T.Text3;
            }
        }

        /// <summary>The programme's picture; else the channel's brand colours (from its logo) with the logo; else a tint.</summary>
        private static Control BuildThumb(Channel ch, Programme? p)
        {
            var box = new Border { Width = 128, Height = 72, CornerRadius = new CornerRadius(10), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center };
            if (p?.ImageUrl is { Length: > 0 } url) { box.Background = T.CardBack; box.Child = new ArtImage(url); return box; }
            var hue = 0; foreach (var c in ch.Id) hue = (hue * 31 + c) % 360;
            box.Background = Card.BrandGradient(HsvColor.FromAhsv(1, hue, 0.6, 0.5).ToRgb());
            if (ch.LogoUrl is { Length: > 0 } logoUrl)
            {
                var logo = new Image { Stretch = Stretch.Uniform, MaxWidth = 96, MaxHeight = 44, Opacity = 0 };
                logo.PropertyChanged += (_, e) =>
                {
                    if (e.Property != Image.SourceProperty || logo.Source == null) return;
                    logo.Opacity = 1;
                    if (logo.Source is Avalonia.Media.Imaging.Bitmap bmp && BrandColour.Of(bmp) is { } brand) box.Background = Card.BrandGradient(brand);
                };
                AsyncImageLoader.ImageLoader.SetSource(logo, logoUrl);
                box.Child = logo;
            }
            else box.Child = new TextBlock { Text = ch.Name, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new Thickness(10), VerticalAlignment = VerticalAlignment.Bottom };
            return box;
        }
    }
}
