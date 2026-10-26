using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// Digits while watching: "1 0 4" shows Channel 4 and what's on, then tunes (OK tunes now). DEL corrects. A number that
    /// doesn't exist says so and tunes nothing.
    /// </summary>
    public sealed class NumberPanel : IScreen
    {
        public static readonly TimeSpan ExactWait = TimeSpan.FromMilliseconds(700);
        public static readonly TimeSpan PartialWait = TimeSpan.FromMilliseconds(1600);
        public static readonly TimeSpan ErrorWait = TimeSpan.FromMilliseconds(1500);

        private readonly NewUiContext _ctx;
        private readonly ITimers _timers;
        private readonly NumberEntry _entry;
        private readonly Panel _root = new() { Width = 1920, Height = 1080 };
        private readonly StackPanel _digits = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
        private readonly TextBlock _name = new() { FontSize = 34, FontWeight = FontWeight.Bold, Foreground = T.Text };
        private readonly TextBlock _sub = new() { FontSize = 24, Foreground = T.Text2, MaxWidth = 520, TextTrimming = TextTrimming.CharacterEllipsis };
        private IDisposable? _timer;

        public Control View => _root;
        public (double X, double Y) EnterFrom => (0, 0);
        public string Text { get; private set; } = "";
        public Channel? Match { get; private set; }
        public string? Error { get; private set; }

        public NumberPanel(NewUiContext ctx, ITimers timers, int first)
        {
            _ctx = ctx; _timers = timers;
            _entry = new NumberEntry(ctx.Channels.Select(c => c.Number));
            _root.Children.Add(new Border
            {
                Background = T.Glass,
                CornerRadius = new CornerRadius(T.RPanel),
                Padding = new Thickness(40, 30),
                Margin = new Thickness(0, 64, T.SafeX, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                MinWidth = 520,
                Child = new StackPanel { Spacing = 18, Children = { _digits, new StackPanel { Spacing = 4, Children = { _name, _sub } } } },
            });
            Draw(_entry.Push(first));
        }

        private void Draw(NumberEntry.Result r)
        {
            Text = r.Text; Error = r.Error;
            Match = r.Match is { } n ? _ctx.Channels.FirstOrDefault(c => c.Number == n) : null;
            _digits.Children.Clear();
            for (var i = 0; i < Math.Max(3, r.Text.Length); i++)
                _digits.Children.Add(new Border
                {
                    Width = 64,
                    Height = 80,
                    CornerRadius = new CornerRadius(12),
                    Background = new SolidColorBrush(Color.Parse(i < r.Text.Length ? "#33FFFFFF" : "#14FFFFFF")),
                    Child = new TextBlock { Text = i < r.Text.Length ? r.Text[i].ToString() : "", FontSize = 48, FontWeight = FontWeight.Bold, Foreground = T.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                });
            _name.Text = Match?.Name ?? Error ?? "";
            _name.Foreground = Error != null ? T.Live : T.Text;
            _sub.Text = Match != null ? "" : "";
            if (Match is { } m) _ = WhatsOnAsync(m);

            _timer?.Dispose();
            var exact = Match != null && !_ctx.Channels.Any(c => c.Number != r.Text && c.Number.StartsWith(r.Text, StringComparison.Ordinal));
            _timer = _timers.After(Error != null ? ErrorWait : exact ? ExactWait : PartialWait, () =>
            {
                var tune = Match;
                Close();
                if (tune != null) _ctx.PlayLive(tune);
            });
        }

        private async System.Threading.Tasks.Task WhatsOnAsync(Channel ch)
        {
            try
            {
                var p = await _ctx.Home.CurrentProgrammeAsync(ch.Id);
                Dispatcher.UIThread.Post(() => { if (Match?.Id == ch.Id) _sub.Text = p != null ? $"{p.Title} · until {p.End.LocalDateTime:HH:mm}" : "No programme information"; });
            }
            catch { /* the name is enough */ }
        }

        private void Close()
        {
            _timer?.Dispose();
            if (_ctx.Nav.State.Overlay?.Name == "number") _ctx.Nav.CloseOverlay();
        }

        public bool OnAction(UiAction a, bool repeat)
        {
            if (InputMap.DigitOf(a) is { } d) { Draw(_entry.Push(d)); return true; }
            switch (a)
            {
                case UiAction.Del or UiAction.Left:
                    var r = _entry.Del();
                    if (r.Text.Length == 0) Close(); else Draw(r);
                    return true;
                case UiAction.Ok:
                    var tune = Match;
                    Close();
                    if (tune != null) _ctx.PlayLive(tune);
                    return true;
                case UiAction.Back: _timer?.Dispose(); return false;
            }
            return a is UiAction.Up or UiAction.Down or UiAction.Right;
        }

        public void Destroy() => _timer?.Dispose();
    }
}
