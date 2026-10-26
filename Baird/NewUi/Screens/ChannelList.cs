using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Playback;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// ▴ ▾ while watching live: a strip on the left listing the channels with what's on. Each press moves one channel; it
    /// tunes once you rest about a second (OK tunes now and closes). Back closes it; left alone it goes after 6 s.
    /// </summary>
    public sealed class ChannelList : IScreen
    {
        public static readonly TimeSpan Rest = TimeSpan.FromSeconds(1);
        public static readonly TimeSpan Idle = TimeSpan.FromSeconds(6);
        private const int Visible = 9;
        private const double RowHeight = 96;

        private readonly NewUiContext _ctx;
        private readonly ITimers _timers;
        private readonly IReadOnlyList<Channel> _channels;
        private readonly Dictionary<string, Programme?> _now = new();
        private readonly Panel _root = new() { Width = 1920, Height = 1080 };
        private readonly StackPanel _rows = new();
        private readonly TextBlock _clock = new() { FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = T.Text2 };
        private int _index;
        private IDisposable? _rest, _idle;

        public Control View => _root;
        public Channel? Lit => _channels.Count > 0 ? _channels[_index] : null;
        public Control? LitElement => _rows.Children.OfType<Control>().FirstOrDefault(c => c.Tag is true);

        public ChannelList(NewUiContext ctx, ITimers timers)
        {
            _ctx = ctx; _timers = timers; _channels = ctx.Channels;
            _index = Math.Max(0, _channels.ToList().FindIndex(c => c.Id == ctx.Playback.Current?.Channel?.Id));

            _root.Children.Add(new Border
            {
                Width = 760,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.Parse("#F2000000"), 0), new GradientStop(Color.Parse("#B3000000"), 0.7), new GradientStop(Colors.Transparent, 1) },
                },
            });
            var panel = new StackPanel { Width = 600, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(T.SafeX, 64, 0, 0), Spacing = 22 };
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 18,
                Children = { new TextBlock { Text = "Channels", FontSize = 34, FontWeight = FontWeight.Bold, Foreground = T.Text }, _clock },
            });
            panel.Children.Add(new Border { ClipToBounds = true, Height = RowHeight * Visible, Child = _rows });
            panel.Children.Add(new TextBlock { Text = "▴ ▾ move · rest to tune · OK tunes now · Back closes", FontSize = 20, Foreground = T.Text3 });
            _root.Children.Add(panel);
            Motion.Animate(_rows, T.Fast);
            Draw();
            Bump();
            _ = LoadProgrammesAsync();
        }

        private async System.Threading.Tasks.Task LoadProgrammesAsync()
        {
            try
            {
                foreach (var card in await _ctx.Home.OnNowAsync(_ctx.Now()))
                    if (card.Channel != null) _now[card.Channel.Id] = card.Programme;
                Dispatcher.UIThread.Post(Draw);
            }
            catch (Exception ex) { Console.WriteLine($"[ChannelList] What's on failed: {ex.Message}"); }
        }

        private void Draw()
        {
            _clock.Text = DateTime.Now.ToString("HH:mm");
            _rows.Children.Clear();
            if (_channels.Count == 0) return;
            // A window around the lit channel, so a long list never builds every row.
            var first = Math.Clamp(_index - Visible / 2, 0, Math.Max(0, _channels.Count - Visible));
            for (var i = first; i < Math.Min(_channels.Count, first + Visible); i++)
            {
                var c = _channels[i];
                var lit = i == _index;
                _now.TryGetValue(c.Id, out var p);
                var sub = p != null ? $"{p.Title} · {p.Start.LocalDateTime:HH:mm}–{p.End.LocalDateTime:HH:mm}" : "No programme information";
                var playing = c.Id == _ctx.Playback.Current?.Channel?.Id;
                _rows.Children.Add(new Border
                {
                    Tag = lit,
                    Height = RowHeight - 8,
                    Margin = new Thickness(0, 0, 0, 8),
                    CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(22, 0),
                    Background = lit ? T.FocusFill : Brushes.Transparent,
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 20,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = c.Number, Width = 70, FontSize = 30, FontWeight = FontWeight.Bold, Foreground = lit ? T.FocusInk : T.Text3, VerticalAlignment = VerticalAlignment.Center },
                            new StackPanel
                            {
                                VerticalAlignment = VerticalAlignment.Center,
                                Children =
                                {
                                    new TextBlock { Text = playing ? $"{c.Name}  ●" : c.Name, FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = lit ? T.FocusInk : T.Text, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 },
                                    new TextBlock { Text = sub, FontSize = 21, Foreground = lit ? T.FocusInk : T.Text2, Opacity = lit ? 0.75 : 1, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 460 },
                                },
                            },
                        },
                    },
                });
            }
        }

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
            if (Lit is { } ch && ch.Id != _ctx.Playback.Current?.Channel?.Id) _ctx.PlayLive(ch);
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
                    _rest = _timers.After(Rest, () => { Tune(); Draw(); });
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

        public void Destroy() { _rest?.Dispose(); _idle?.Dispose(); }
    }

    /// <summary>Live TV's "up next" corner card: in a programme's last 5 minutes, once per programme.</summary>
    public sealed class LiveUpNext
    {
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
        private string? _dismissed;

        public bool ShouldShow(Programme? now, DateTimeOffset at) =>
            now != null && now.Id != _dismissed && at < now.End && now.End - at <= Window && at >= now.Start;

        public void Dismiss(Programme? p) => _dismissed = p?.Id;
    }
}
