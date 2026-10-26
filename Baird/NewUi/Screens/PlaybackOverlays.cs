using System;
using System.Collections.Generic;
using System.Linq;
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
                Background = T.Glass,
                CornerRadius = new CornerRadius(26),
                Padding = new Thickness(40, 26),
                MinWidth = 760,
                Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 120),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 30, Children = { _num, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _name, _prog } }, _spinner } },
            };
            Motion.Animate(_dim, T.Fast);
            Motion.Animate(_card, T.Fast);
            Children.Add(_dim);
            Children.Add(_card);
        }

        public void Show(Channel ch, Programme? now) =>
            Present(ch.Number, ch.Name, now != null ? $"{now.Title} · until {now.End.LocalDateTime:HH:mm}" : "No programme information", spinner: true, cap: TimeSpan.FromSeconds(12));

        /// <summary>An on-demand title loading: its name and the current step ("Signing in to Netflix…"). Up until it loads.</summary>
        public void ShowStatus(string title, string status) => Present("", title, status, spinner: true, cap: null);

        /// <summary>A play that failed: stays up (dimmed picture) until the viewer moves on.</summary>
        public void ShowFailure(string title, string message) => Present("", title, message, spinner: false, cap: null);

        private void Present(string number, string name, string sub, bool spinner, TimeSpan? cap)
        {
            _num.Text = number;
            _num.IsVisible = number.Length > 0;
            _name.Text = name;
            _prog.Text = sub;
            _prog.TextWrapping = TextWrapping.Wrap;
            _prog.MaxWidth = 1100;
            _spinner.IsVisible = spinner;
            _shownAt = DateTime.UtcNow; _loaded = false;
            _dim.Opacity = 0.75; _card.Opacity = 1;
            _hide?.Dispose();
            _hide = null;
            if (_spin == null)
            {
                _spin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                _spin.Tick += (_, _) => _spinner.StartAngle = (_spinner.StartAngle + 12) % 360;
            }
            if (spinner && !Motion.Lite) _spin.Start(); else _spin.Stop();
            // A tune never stays up forever (a failed one shows its own message).
            if (cap is { } c) _hide = DispatcherTimer.RunOnce(Hide, c);
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

    /// <summary>
    /// OK while watching: what's on (and next, for live), progress and time behind live, and buttons ⏯ · Subtitles · Audio
    /// (◂ ▸ choose, OK presses). ⏮ ⏭ still scan; ▴ ▾ on live swap it for the channel list. Auto-hides after 5 s.
    /// </summary>
    public sealed class InfoBar : IScreen
    {
        private readonly NewUiContext _ctx;
        private readonly Panel _root = new() { Width = 1920, Height = 1080 };
        private readonly StackPanel _bar = new() { Spacing = 10 };
        private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
        private IDisposable? _idle;
        private Programme? _next;
        private int _button;
        private string? _audio;

        public Control View => _root;
        public IReadOnlyList<string> Buttons => new[]
        {
            _ctx.Playback.IsPaused ? "Play" : "Pause",
            _ctx.Playback.SubtitlesOn ? "Subtitles on" : "Subtitles off",
            _audio ?? "Audio",
        };
        public int LitButton => _button;

        public InfoBar(NewUiContext ctx)
        {
            _ctx = ctx;
            _root.Children.Add(new Border { Height = 560, VerticalAlignment = VerticalAlignment.Bottom, Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative), GradientStops = { new GradientStop(Color.Parse("#BF000000"), 0), new GradientStop(Colors.Transparent, 1) } } });
            _root.Children.Add(new Border { Background = T.Glass, CornerRadius = new CornerRadius(T.RPanel), Padding = new Thickness(44, 34, 44, 30), Margin = new Thickness(64, 0, 64, 48), VerticalAlignment = VerticalAlignment.Bottom, Child = _bar });
            _tick.Tick += (_, _) => Draw();
            _tick.Start();
            Bump();
            Draw();
            if (ctx.Playback.Current?.Channel is { } ch) _ = LoadNextAsync(ch.Id);
        }

        private async System.Threading.Tasks.Task LoadNextAsync(string channelId)
        {
            var next = await _ctx.Home.NextProgrammeAsync(channelId, _ctx.Now());
            Dispatcher.UIThread.Post(() => { _next = next; Draw(); });
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
            if (top.Children.Count > 0) _bar.Children.Add(top);
            _bar.Children.Add(new TextBlock { Text = now.Kind == "live" ? _ctx.Programme?.Title ?? "No programme information" : now.Title, FontSize = 56, FontWeight = FontWeight.Bold, Foreground = T.Text, Margin = new Thickness(0, 4, 0, 14) });

            double frac; string left, mid, right;
            if (now.Kind == "live")
            {
                var prog = _ctx.Programme;
                if (prog != null)
                {
                    frac = prog.ProgressAt(_ctx.Now() - p.BehindLive);
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
            const double trackWidth = 1920 - 128 - 88;
            var track = new Grid { Height = 8 };
            track.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#2EFFFFFF")), CornerRadius = new CornerRadius(4) });
            track.Children.Add(new Border { Background = T.Text, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Clamp(frac, 0, 1) * trackWidth });
            if (now.Kind == "live" && _ctx.Programme is { } lp)
                // The live edge, in red, when time-shifted.
                track.Children.Add(new Border { Background = T.Live, Width = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(Math.Clamp(lp.ProgressAt(_ctx.Now()), 0, 1) * trackWidth - 2, -4, 0, -4), IsVisible = p.BehindLive > TimeSpan.FromSeconds(30) });
            _bar.Children.Add(track);
            var times = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            times.Children.Add(new TextBlock { Text = left, FontSize = 22, Foreground = T.Text2 });
            times.Children.Add(new TextBlock { Text = mid, FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = T.Text, HorizontalAlignment = HorizontalAlignment.Center });
            times.Children.Add(new TextBlock { Text = right, FontSize = 22, Foreground = T.Text2, HorizontalAlignment = HorizontalAlignment.Right });
            _bar.Children.Add(times);

            var row = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            var labels = Buttons;
            for (var i = 0; i < labels.Count; i++)
            {
                var lit = i == _button;
                buttons.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(999),
                    Padding = new Thickness(24, 10),
                    Background = lit ? T.FocusFill : T.Pill,
                    Child = new TextBlock { Text = labels[i], FontSize = 23, FontWeight = FontWeight.SemiBold, Foreground = lit ? T.FocusInk : T.Text },
                });
            }
            row.Children.Add(buttons);
            if (now.Kind == "live" && _next != null)
                row.Children.Add(new TextBlock { Text = $"Next: {_next.Start.LocalDateTime:HH:mm}  {_next.Title}", FontSize = 24, Foreground = T.Text2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            _bar.Children.Add(row);
        }

        public bool OnAction(UiAction a, bool repeat)
        {
            Bump();
            switch (a)
            {
                case UiAction.Left or UiAction.Right:
                    _button = Math.Clamp(_button + (a == UiAction.Left ? -1 : 1), 0, Buttons.Count - 1); Draw(); return true;
                case UiAction.Ok:
                    if (_button == 0) _ctx.Playback.TogglePause();
                    else if (_button == 1) _ctx.Playback.ToggleSubtitles();
                    else _audio = _ctx.CycleAudio() ?? "Only one audio track";
                    Draw(); return true;
                case UiAction.PlayPause: _ctx.Playback.TogglePause(); Draw(); return true;
                case UiAction.Prev or UiAction.Next: _ctx.Playback.SeekRelative(TimeSpan.FromSeconds(a == UiAction.Prev ? -10 : 30)); Draw(); return true;
                case UiAction.Up or UiAction.Down when _ctx.Playback.Current?.Kind == "live" && _ctx.Channels.Count > 0:
                    _ctx.Nav.OpenOverlay(new OverlayRef("channels")); return true;
            }
            return false;
        }

        public void Destroy() { _tick.Stop(); _idle?.Dispose(); }
    }

    /// <summary>
    /// Hold OK while watching: actions for what's playing, in a glass panel from the right. Subtitles first (used most);
    /// Audio; Start over (on demand); This channel's schedule (live).
    /// </summary>
    public sealed class ActionsPanel : IScreen
    {
        private readonly NewUiContext _ctx;
        private readonly Action _openGuide;
        private readonly Panel _root = new() { Width = 1920, Height = 1080 };
        private readonly StackPanel _list = new() { Spacing = 8 };
        private readonly List<(string Label, Action Run)> _items = new();
        private int _index;

        public Control View => _root;
        public IReadOnlyList<string> Items => _items.Select(i => i.Label).ToList();
        public Control? LitElement => _list.Children.Count > _index ? _list.Children[_index] as Control : null;

        public ActionsPanel(NewUiContext ctx, Action openGuide)
        {
            _ctx = ctx; _openGuide = openGuide;
            _root.Children.Add(new Border
            {
                Width = 640,
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = T.Glass,
                Padding = new Thickness(48, 120, 48, 48),
                Child = new StackPanel
                {
                    Spacing = 28,
                    Children = { new TextBlock { Text = ctx.Playback.Current?.Title ?? "", FontSize = 34, FontWeight = FontWeight.Bold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap }, _list },
                },
            });
            Build();
        }

        private void Build()
        {
            var p = _ctx.Playback;
            _items.Clear();
            _items.Add((p.SubtitlesOn ? "Subtitles off" : "Subtitles on", p.ToggleSubtitles));
            _items.Add(("Next audio track", () => _ctx.Toast(_ctx.CycleAudio() ?? "There's only one audio track")));
            if (p.Current?.Kind == "vod") _items.Add(("Start over", p.StartOver));
            if (p.Current?.Kind == "live") _items.Add(("This channel's schedule", _openGuide));
            Draw();
        }

        private void Draw()
        {
            _list.Children.Clear();
            for (var i = 0; i < _items.Count; i++)
            {
                var lit = i == _index;
                _list.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(26, 18),
                    Background = lit ? T.FocusFill : Brushes.Transparent,
                    Child = new TextBlock { Text = _items[i].Label, FontSize = 28, FontWeight = FontWeight.SemiBold, Foreground = lit ? T.FocusInk : T.Text },
                });
            }
        }

        public bool OnAction(UiAction a, bool repeat)
        {
            switch (a)
            {
                case UiAction.Up or UiAction.Down:
                    _index = Math.Clamp(_index + (a == UiAction.Up ? -1 : 1), 0, _items.Count - 1); Draw(); return true;
                case UiAction.Ok:
                    var run = _items[_index].Run;
                    if (_ctx.Nav.State.Overlay?.Name == "actions") _ctx.Nav.CloseOverlay();
                    run();
                    return true;
                case UiAction.Left or UiAction.Right: return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The service asked for a code it sent the viewer (Netflix after a sign-in): type it with the number keys, ◂ deletes,
    /// OK sends it, Back gives up on the play.
    /// </summary>
    public sealed class CodeEntry : IScreen
    {
        public const int Length = 6;
        private readonly Func<string?> _request;
        private readonly Func<string, System.Threading.Tasks.Task> _submit;
        private readonly Action _cancel;
        private readonly Panel _root = new() { Width = 1920, Height = 1080, Background = new SolidColorBrush(Color.Parse("#B3000000")) };
        private readonly TextBlock _ask = new() { FontSize = 30, Foreground = T.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 1100, TextAlignment = TextAlignment.Center };
        private readonly StackPanel _boxes = new() { Orientation = Orientation.Horizontal, Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center };
        private string _code = "";
        public string Code => _code;
        public Control View => _root;

        public CodeEntry(Func<string?> request, Func<string, System.Threading.Tasks.Task> submit, Action cancel)
        {
            _request = request; _submit = submit; _cancel = cancel;
            _root.Children.Add(new Border
            {
                Background = T.Glass,
                CornerRadius = new CornerRadius(T.RPanel),
                Padding = new Thickness(60, 48),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new StackPanel
                {
                    Spacing = 34,
                    Children =
                    {
                        _ask, _boxes,
                        new TextBlock { Text = "Type it with the number keys · ◂ deletes · OK sends · Back cancels", FontSize = 22, Foreground = T.Text2, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            });
            Draw();
        }

        public void Refresh() => Draw();

        private void Draw()
        {
            _ask.Text = _request() ?? "Enter the code you were sent";
            _boxes.Children.Clear();
            for (var i = 0; i < Length; i++)
                _boxes.Children.Add(new Border
                {
                    Width = 84,
                    Height = 104,
                    CornerRadius = new CornerRadius(14),
                    Background = new SolidColorBrush(Color.Parse("#26FFFFFF")),
                    BorderBrush = i == _code.Length ? Brushes.White : Brushes.Transparent,
                    BorderThickness = new Thickness(3),
                    Child = new TextBlock { Text = i < _code.Length ? _code[i].ToString() : "", FontSize = 52, FontWeight = FontWeight.Bold, Foreground = T.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
        }

        public bool OnAction(UiAction a, bool repeat)
        {
            if (InputMap.DigitOf(a) is { } d) { if (_code.Length < Length) _code += d; Draw(); return true; }
            switch (a)
            {
                case UiAction.Left or UiAction.Del: if (_code.Length > 0) _code = _code[..^1]; Draw(); return true;
                case UiAction.Ok: if (_code.Length > 0) { var code = _code; _code = ""; Draw(); _ = _submit(code); } return true;
                case UiAction.Back: _cancel(); return true;
            }
            return true; // nothing else gets past the question
        }
    }

}
