using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Baird.NewUi.Adapters;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// ▴ ▾ while watching live (prototype js/overlays/channels.js): a glass panel on the left listing the channels — a
    /// thumbnail, what's on and a to-scale timeline of the next 3 hours each. One highlight slides between rows and the
    /// list scrolls under it (rows are built once, only around the visible window). Each press moves one channel; it
    /// tunes once you rest about a second (OK tunes now and closes). Back closes it; left alone it goes after 6 s.
    /// </summary>
    public sealed class ChannelList : IScreen
    {
        public static readonly TimeSpan Rest = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan Idle = TimeSpan.FromSeconds(6);
        public static readonly TimeSpan FirstStep = TimeSpan.FromMilliseconds(260);
        public const int Visible = 7;
        public const double Row = 112;

        private readonly NewUiContext _ctx;
        private readonly ITimers _timers;
        private readonly IReadOnlyList<Channel> _channels;
        private readonly Dictionary<string, IReadOnlyList<Programme>> _schedules = new();
        private readonly Dictionary<int, ChannelRow> _rows = new();
        private HashSet<int> _prevWanted = new();
        private readonly Panel _root = new() { Width = 1920, Height = 1080 };
        private readonly Canvas _track = new();
        private readonly Border _hl;
        private int _index;
        private bool _animating;
        private IDisposable? _rest, _idle, _firstStep;

        public Control View => _root;
        public Channel? Lit => _channels.Count > 0 ? _channels[_index] : null;
        /// <summary>The highlight (one Border that slides from row to row).</summary>
        public Control? LitElement => _channels.Count > 0 ? _hl : null;
        public IReadOnlyCollection<ChannelRow> Rows => _rows.Values;
        public ChannelRow? RowOf(string channelId) => _rows.Values.FirstOrDefault(r => r.Channel.Id == channelId);

        public ChannelList(NewUiContext ctx, ITimers timers, UiAction? step = null, Func<DateTimeOffset, Task<GuideModel>>? guide = null)
        {
            _ctx = ctx; _timers = timers; _channels = ctx.Channels;
            _index = Math.Max(0, _channels.ToList().FindIndex(c => c.Id == ctx.Playback.Current?.Channel?.Id));

            _hl = new Border { Width = ChannelRow.Width0, Height = ChannelRow.Height0, CornerRadius = new CornerRadius(16), Background = T.FocusFill, BoxShadow = BoxShadows.Parse("0 14 34 0 #73000000"), ZIndex = 0 };
            _hl.Classes.Add("focused");
            _track.Children.Add(_hl);
            var window = new Panel { ClipToBounds = true, Height = Visible * Row, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(18, 26, 18, 0), Children = { _track } };
            var hint = new TextBlock { Text = "▴ ▾ move · rest to tune · OK tunes now · Back closes", FontSize = 20, Foreground = T.Text3, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(34, 0, 0, 28) };
            _root.Children.Add(new Border
            {
                Width = 640,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(48),
                CornerRadius = new CornerRadius(T.RPanel),
                ClipToBounds = true,
                Background = T.Glass,
                Child = new Panel { Children = { window, hint } },
            });
            Draw();
            Bump();
            _ = LoadAsync(guide);
            // Opened by ▴ / ▾: that press also moves one channel, a moment after the list arrives.
            if (step is UiAction.Up or UiAction.Down) _firstStep = _timers.After(FirstStep, () => OnAction(step.Value, false));
        }

        /// <summary>Schedules from the guide (now, next and the timeline), else what's on now from On now.</summary>
        private async Task LoadAsync(Func<DateTimeOffset, Task<GuideModel>>? guide)
        {
            try
            {
                if (guide != null)
                    foreach (var c in (await guide(_ctx.Now())).Channels) _schedules[c.Channel.Id] = c.Schedule;
                else
                    foreach (var card in await _ctx.Home.OnNowAsync(_ctx.Now()))
                        if (card.Channel != null && card.Programme != null) _schedules[card.Channel.Id] = new[] { card.Programme };
                if (Dispatcher.UIThread.CheckAccess()) Rebuild(); else Dispatcher.UIThread.Post(Rebuild);
            }
            catch (Exception ex) { Console.WriteLine($"[ChannelList] What's on failed: {ex.Message}"); }
        }

        /// <summary>The data arrived: build the visible rows again, once.</summary>
        private void Rebuild()
        {
            foreach (var r in _rows.Values) _track.Children.Remove(r);
            _rows.Clear(); _prevWanted.Clear();
            Draw();
        }

        private void Draw()
        {
            if (_channels.Count == 0) return;
            var n = _channels.Count;
            var first = Math.Clamp(_index - 3, 0, Math.Max(0, n - Visible));

            // Rows around the window; keep the last window's while the track slides away from them.
            var wanted = new HashSet<int>();
            for (var j = Math.Max(0, first - 1); j < Math.Min(n, first + Visible + 1); j++)
            {
                wanted.Add(j);
                if (_rows.ContainsKey(j)) continue;
                var c = _channels[j];
                var row = new ChannelRow(c, _schedules.TryGetValue(c.Id, out var s) ? s : Array.Empty<Programme>(), _ctx.Now(), c.Id == _ctx.Playback.Current?.Channel?.Id) { ZIndex = 1 };
                Canvas.SetTop(row, j * Row);
                _rows[j] = row;
                _track.Children.Add(row);
            }
            foreach (var j in _rows.Keys.Where(k => !wanted.Contains(k) && !_prevWanted.Contains(k)).ToList())
            {
                _track.Children.Remove(_rows[j]);
                _rows.Remove(j);
            }
            _prevWanted = wanted;
            foreach (var (j, row) in _rows) row.Lit = j == _index;

            _hl.RenderTransform = TranslateY(_index * Row);
            _track.RenderTransform = TranslateY(-first * Row);
            if (!_animating)
            {
                _animating = true; // the first placement doesn't glide
                _hl.Transitions = new Transitions { new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Motion.Lite ? T.Fast : TimeSpan.FromMilliseconds(300), Easing = T.Ease } };
                _track.Transitions = new Transitions { new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Motion.Med, Easing = T.Ease } };
            }
        }

        private static ITransform TranslateY(double y) => TransformOperations.Parse(string.Create(CultureInfo.InvariantCulture, $"translate(0px, {y:0.##}px)"));

        private void Bump()
        {
            _idle?.Dispose();
            _idle = _timers.After(Idle, Close);
        }

        private void Close()
        {
            _rest?.Dispose();
            if (_ctx.Nav.State.Overlay?.Name == "channels") _ctx.Nav.CloseOverlay();
        }

        private void Tune()
        {
            _rest?.Dispose();
            if (Lit is not { } ch || ch.Id == _ctx.Playback.Current?.Channel?.Id) return;
            _ctx.PlayLive(ch);
            foreach (var r in _rows.Values) r.Watching = r.Channel.Id == ch.Id;
        }

        public bool OnAction(UiAction a, bool repeat)
        {
            switch (a)
            {
                case UiAction.Up or UiAction.Down:
                    if (_channels.Count == 0) return true;
                    _index = ((_index + (a == UiAction.Down ? 1 : -1)) % _channels.Count + _channels.Count) % _channels.Count;
                    Draw();
                    Bump();
                    _rest?.Dispose();
                    _rest = _timers.After(Rest, Tune);
                    return true;
                case UiAction.Ok:
                    Tune();
                    Close();
                    return true;
                case UiAction.Back:
                    _rest?.Dispose(); _idle?.Dispose();
                    return false; // the shell closes the overlay
            }
            return false;
        }

        public void Destroy() { _rest?.Dispose(); _idle?.Dispose(); _firstStep?.Dispose(); }
    }

    /// <summary>Live TV's "up next" corner card: in a programme's last 5 minutes, once per programme.</summary>
    public sealed class LiveUpNext
    {
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
        private string? _dismissed;

        public bool ShouldShow(Programme? now, DateTimeOffset at) =>
            now != null && now.Id != _dismissed && at < now.End && now.End - at <= Window && at >= now.Start;

        public void Dismiss(Programme? p) => _dismissed = p?.Id;

        /// <summary>The card's words (prototype nearend.js): "Up next on BBC One · in 4 min", the next title, "then …".</summary>
        public static (string Eyebrow, string Title, string? Then) Words(Channel ch, Programme current, Programme next, Programme? after, DateTimeOffset now)
        {
            var mins = Math.Max(1, (int)Math.Ceiling((current.End - now).TotalMinutes));
            return ($"Up next on {ch.Name} · in {mins} min",
                $"{next.Start.ToLocalTime():HH:mm}  {next.Title}",
                after == null ? null : $"then {after.Start.ToLocalTime():HH:mm} {after.Title}");
        }

        /// <summary>The words as styled lines for one TextBlock (eyebrow small, title large, then, and the keys).</summary>
        public static InlineCollection Inlines(Channel ch, Programme current, Programme next, Programme? after, DateTimeOffset now)
        {
            var (eyebrow, title, then) = Words(ch, current, next, after, now);
            var lines = new InlineCollection
            {
                new Run(eyebrow) { FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = T.Text2 },
                new LineBreak(),
                new Run(title) { FontSize = 36, FontWeight = FontWeight.Bold, Foreground = T.Text },
                new LineBreak(),
            };
            if (then != null) { lines.Add(new Run(then) { FontSize = 24, Foreground = T.Text2 }); lines.Add(new LineBreak()); }
            lines.Add(new Run("OK to stay · ▴ ▾ for something else") { FontSize = 20, Foreground = T.Text3 });
            return lines;
        }
    }
}
