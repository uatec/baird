using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// The tuning transition: while the tuner locks (1–2 s on DVB) the picture is dimmed and an ident shows the channel,
    /// what's on and a spinner; it fades once the stream has loaded (and at least ~1.5 s have passed).
    /// </summary>
    public sealed class TuneIdent : Panel
    {
        private readonly Border _dim = new() { Background = Brushes.Black, Opacity = 0 };
        private readonly Border _card;
        private readonly TextBlock _num = new() { FontSize = 72, FontWeight = FontWeight.Bold, Foreground = T.Text2, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock _name = new() { FontSize = 40, FontWeight = FontWeight.Bold, Foreground = T.Text };
        private readonly TextBlock _prog = new() { FontSize = 26, Foreground = T.Text2 };
        private readonly Arc _spinner = new() { Width = 44, Height = 44, StartAngle = 0, SweepAngle = 90, Stroke = Brushes.White, StrokeThickness = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(30, 0, 0, 0) };
        private DateTime _shownAt;
        private bool _loaded;
        private IDisposable? _hide;
        private DispatcherTimer? _spin;

        public TuneIdent()
        {
            IsHitTestVisible = false;
            _card = new Border
            {
                Background = T.Glass, CornerRadius = new CornerRadius(26), Padding = new Thickness(40, 26), MinWidth = 760, Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 120),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 30, Children = { _num, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _name, _prog } }, _spinner } },
            };
            Motion.Animate(_dim, T.Fast);
            Motion.Animate(_card, T.Fast);
            Children.Add(_dim);
            Children.Add(_card);
        }

        public void Show(Channel ch, Programme? now)
        {
            _num.Text = ch.Number;
            _name.Text = ch.Name;
            _prog.Text = now != null ? $"{now.Title} · until {now.End.LocalDateTime:HH:mm}" : "No programme information";
            _shownAt = DateTime.UtcNow; _loaded = false;
            _dim.Opacity = 0.75; _card.Opacity = 1;
            _hide?.Dispose();
            if (_spin == null)
            {
                _spin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                _spin.Tick += (_, _) => _spinner.StartAngle = (_spinner.StartAngle + 12) % 360;
            }
            if (!Motion.Lite) _spin.Start();
            // Never stay up forever (a failed tune shows its own message).
            _hide = DispatcherTimer.RunOnce(Hide, TimeSpan.FromSeconds(12));
        }

        /// <summary>The stream has loaded: fade once the minimum time has passed, so the ident never flashes.</summary>
        public void Loaded()
        {
            if (_loaded) return;
            _loaded = true;
            var wait = TimeSpan.FromMilliseconds(1500) - (DateTime.UtcNow - _shownAt);
            _hide?.Dispose();
            _hide = DispatcherTimer.RunOnce(Hide, wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1));
        }

        public void Hide()
        {
            _dim.Opacity = 0; _card.Opacity = 0;
            _spin?.Stop();
        }

        public bool Showing => _card.Opacity > 0;
    }

    /// <summary>OK while watching: what's on, progress (and time behind live), auto-hides after 5 s.</summary>
    public sealed class InfoBar : IScreen
    {
        private readonly NewUiContext _ctx;
        private readonly Panel _root = new() { Width = 1920, Height = 1080 };
        private readonly StackPanel _bar = new() { Spacing = 10 };
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
        private IDisposable? _idle;

        public Control View => _root;

        public InfoBar(NewUiContext ctx)
        {
            _ctx = ctx;
            _root.Children.Add(new Border { Height = 520, VerticalAlignment = VerticalAlignment.Bottom, Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#BF000000"), 0), new GradientStop(Colors.Transparent, 1) } } });
            _root.Children.Add(new Border { Background = T.Glass, CornerRadius = new CornerRadius(T.RPanel), Padding = new Thickness(44, 34, 44, 30), Margin = new Thickness(64, 0, 64, 48), VerticalAlignment = VerticalAlignment.Bottom, Child = _bar });
            _tick.Tick += (_, _) => Draw();
            _tick.Start();
            Bump();
            Draw();
        }

        private void Bump()
        {
            _idle?.Dispose();
            _idle = DispatcherTimer.RunOnce(() => { if (_ctx.Nav.State.Overlay?.Name == "info") _ctx.Nav.CloseOverlay(); }, TimeSpan.FromSeconds(5));
        }

        private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        private void Draw()
        {
            var p = _ctx.Playback;
            var now = p.Current;
            if (now == null) return;
            _bar.Children.Clear();
            var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            if (now.Channel != null) top.Children.Add(new TextBlock { Text = now.Channel.Number, FontSize = 40, FontWeight = FontWeight.Bold, Foreground = T.Text3 });
            if (now.Channel != null) top.Children.Add(new TextBlock { Text = now.Channel.Name, FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = T.Text, VerticalAlignment = VerticalAlignment.Center });
            if (now.Kind == "live") top.Children.Add(new Border { Background = T.Live, CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 2), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "Live", FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = T.Text } });
            if (p.IsPaused) top.Children.Add(new TextBlock { Text = "❚❚ Paused", FontSize = 24, Foreground = T.Text2, VerticalAlignment = VerticalAlignment.Center });
            if (top.Children.Count > 0) _bar.Children.Add(top);
            _bar.Children.Add(new TextBlock { Text = now.Kind == "live" ? _ctx.Programme?.Title ?? "No programme information" : now.Title, FontSize = 56, FontWeight = FontWeight.Bold, Foreground = T.Text, Margin = new Thickness(0, 4, 0, 14) });

            double frac; string left, mid, right;
            if (now.Kind == "live")
            {
                var prog = _ctx.Programme;
                if (prog != null)
                {
                    var at = _ctx.Now() - p.BehindLive;
                    frac = prog.ProgressAt(at);
                    left = prog.Start.LocalDateTime.ToString("HH:mm"); right = prog.End.LocalDateTime.ToString("HH:mm");
                }
                else { frac = 0; left = ""; right = ""; }
                mid = p.BehindLive > TimeSpan.FromSeconds(30) ? $"{(int)p.BehindLive.TotalMinutes} min behind live" : "Live";
            }
            else
            {
                frac = p.Duration > TimeSpan.Zero ? p.Position / p.Duration : 0;
                left = Fmt(p.Position); right = $"−{Fmt(p.Duration - p.Position)}"; mid = p.Duration > TimeSpan.Zero ? $"Ends at {DateTime.Now + (p.Duration - p.Position):HH:mm}" : "";
            }
            var track = new Grid { Height = 8 };
            track.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#2EFFFFFF")), CornerRadius = new CornerRadius(4) });
            track.Children.Add(new Border { Background = T.Text, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Clamp(frac, 0, 1) * (1920 - 128 - 88) });
            _bar.Children.Add(track);
            var times = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            times.Children.Add(new TextBlock { Text = left, FontSize = 22, Foreground = T.Text2 });
            times.Children.Add(new TextBlock { Text = mid, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = T.Text, HorizontalAlignment = HorizontalAlignment.Center });
            times.Children.Add(new TextBlock { Text = right, FontSize = 22, Foreground = T.Text2, HorizontalAlignment = HorizontalAlignment.Right });
            _bar.Children.Add(times);
        }

        public bool OnAction(UiAction a, bool repeat)
        {
            Bump();
            switch (a)
            {
                case UiAction.Ok or UiAction.PlayPause: _ctx.Playback.TogglePause(); Draw(); return true;
                case UiAction.Left or UiAction.Right: _ctx.Playback.SeekRelative(TimeSpan.FromSeconds((a == UiAction.Left ? -1 : 1) * (repeat ? 120 : 30))); Draw(); return true;
                case UiAction.Prev or UiAction.Next: _ctx.Playback.SeekRelative(TimeSpan.FromSeconds(a == UiAction.Prev ? -10 : 30)); Draw(); return true;
            }
            return false;
        }

        public void Destroy() { _tick.Stop(); _idle?.Dispose(); }
    }
}
