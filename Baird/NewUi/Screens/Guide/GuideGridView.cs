using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Baird.NewUi.Adapters;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// The guide's grid: channels down the side, time across, a red now line, and a highlight that slides from cell to
    /// cell. The grid scrolls only when focus nears an edge. Only the cells in (and just around) the visible window
    /// exist: rows one either side, an hour either side, plus whatever the previous window showed while it slides away.
    /// </summary>
    public sealed class GuideGridView : Panel
    {
        public const double ChannelsTop = 548, RulerTop = 506, TimeStart = 376, RowHeight = 84, CellHeight = 76, PxPerMin = 12;
        public const int Visible = 6;
        public static readonly TimeSpan Span = TimeSpan.FromMinutes(120);
        private static readonly TimeSpan Margin2 = TimeSpan.FromMinutes(60);
        private static readonly TimeSpan Half = TimeSpan.FromMinutes(30);

        private readonly Canvas _track = new();
        private readonly Canvas _rulerTrack = new();
        private readonly Canvas _chTrack = new();
        private readonly Border _hl;
        private readonly Panel _now;
        private readonly Dictionary<string, GuideCell> _cells = new();
        private readonly Dictionary<int, Control> _chLabels = new();
        private HashSet<string> _prevCells = new();
        private HashSet<int> _prevRows = new();
        private GuideModel? _model;
        private DateTimeOffset? _winStart;
        private int _firstCh;
        private bool _animating;

        public DateTimeOffset WinStart => _winStart ?? _model?.Start ?? default;
        public int FirstChannel => _firstCh;
        public int RealisedCells => _cells.Count;
        public GuideCell? LitCell { get; private set; }
        public Control Highlight => _hl;

        private static TimeSpan Slide => Shell.Motion.Lite ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromMilliseconds(460);

        public GuideGridView()
        {
            Width = 1920; Height = 1080;
            IsHitTestVisible = false;

            _hl = new Border
            {
                Height = CellHeight,
                CornerRadius = new CornerRadius(12),
                Background = T.FocusFill,
                BoxShadow = BoxShadows.Parse("0 0 0 3 #99FFFFFF, 0 0 34 0 #40FFFFFF, 0 16 40 0 #99000000"),
                ZIndex = 0,
            };
            _track.Children.Add(_hl);

            _now = new Panel { Width = 13, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-5, ChannelsTop - 28, 0, 0), Height = 1080 - ChannelsTop + 28, Opacity = 0 };
            _now.Children.Add(new Border { Width = 3, Background = T.Live, HorizontalAlignment = HorizontalAlignment.Center, BoxShadow = BoxShadows.Parse("0 0 16 0 #B3FF3B30") });
            _now.Children.Add(new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(7), Background = T.Live, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -6, 0, 0) });

            Children.Add(Clip(TimeStart, RulerTop, 1920 - TimeStart, 36, _rulerTrack));
            Children.Add(Clip(0, ChannelsTop, TimeStart, 1080 - ChannelsTop, _chTrack));
            Children.Add(Clip(TimeStart, ChannelsTop, 1920 - TimeStart, 1080 - ChannelsTop, _track));
            Children.Add(_now);
            Children.Add(new Border
            {
                Height = 90,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative), GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Transparent, 1) } },
            });
        }

        private static Panel Clip(double left, double top, double width, double height, Control child) => new()
        {
            Width = width,
            Height = height,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(left, top, 0, 0),
            Children = { child },
        };

        private double X(DateTimeOffset t) => (t - _model!.Start).TotalMinutes * PxPerMin;
        private static double Y(int row) => row * RowHeight;

        public void SetModel(GuideModel model)
        {
            _model = model;
            foreach (var c in _cells.Values) _track.Children.Remove(c);
            _cells.Clear(); _prevCells.Clear();
            _chTrack.Children.Clear(); _chLabels.Clear(); _prevRows.Clear();
            _rulerTrack.Children.Clear();
            for (var t = model.Start; t < model.End; t += Half)
            {
                var label = new TextBlock { Text = t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = T.Text3 };
                Canvas.SetLeft(label, X(t));
                _rulerTrack.Children.Add(label);
            }
            _winStart = null;
            _firstCh = 0;
        }

        /// <summary>Lay the grid out around the lit programme. <paramref name="focused"/> = false while the tab bar is lit.</summary>
        public void Draw(GuidePos pos, Programme p, DateTimeOffset now, bool focused)
        {
            if (_model == null || _model.Channels.Count == 0) return;
            var n = _model.Channels.Count;

            // Time window: move only when focus nears an edge, keeping half an hour of context.
            var focusT = p.IsOnAt(now) ? Max(p.Start, now - Half) : Max(p.Start, _model.Start);
            var ws = _winStart ?? _model.Start;
            if (_winStart == null || focusT < ws + TimeSpan.FromMinutes(10) || focusT > ws + Span - TimeSpan.FromMinutes(40))
            {
                var t = focusT - Half;
                var floored = t - TimeSpan.FromTicks((t - _model.Start).Ticks % Half.Ticks);
                _winStart = Max(_model.Start, floored);
            }
            var winStart = _winStart!.Value;
            // Channel window: move only when focus is within one channel of the edge.
            if (pos.Ch < _firstCh + 1) _firstCh = Math.Max(0, pos.Ch - 1);
            if (pos.Ch > _firstCh + Visible - 2) _firstCh = pos.Ch - (Visible - 2);
            _firstCh = Math.Clamp(_firstCh, 0, Math.Max(0, n - Visible));

            Realise(winStart, n);

            var tOff = -X(winStart);
            var cOff = -Y(_firstCh);
            _track.RenderTransform = Translate(tOff, cOff);
            _rulerTrack.RenderTransform = Translate(tOff, 0);
            _chTrack.RenderTransform = Translate(0, cOff);
            _now.RenderTransform = Translate(TimeStart + X(now) + tOff, 0);
            _now.Opacity = now > winStart && now < winStart + Span ? 1 : 0;

            // The highlight slides to the lit programme, clipped to the window so long programmes still read.
            var a = Max(p.Start, winStart);
            var b = p.NoInfo ? winStart + Span : Min(p.End, winStart + Span);
            if (b < a + TimeSpan.FromMinutes(15)) b = a + TimeSpan.FromMinutes(15);
            _hl.RenderTransform = Translate(X(a), Y(pos.Ch));
            _hl.Width = Math.Max(4, (b - a).TotalMinutes * PxPerMin - 8);
            _hl.Opacity = focused ? 1 : 0;

            LitCell = null;
            foreach (var cell in _cells.Values)
            {
                var lit = focused && cell.Programme.Id == p.Id && cell.Programme.ChannelId == p.ChannelId;
                cell.Lit = lit;
                if (lit) LitCell = cell;
                cell.Past = cell.Programme.End <= now;
                cell.Inset = cell.Programme.Start < winStart && cell.Programme.End > winStart ? (winStart - cell.Programme.Start).TotalMinutes * PxPerMin : 0;
            }
            foreach (var (row, label) in _chLabels) label.Opacity = row == pos.Ch ? 1 : 0.62;

            if (!_animating)
            {
                // From the first layout on, movement glides (the first placement doesn't).
                _animating = true;
                foreach (var v in new Visual[] { _track, _rulerTrack, _chTrack, _now }) Glide(v);
                _hl.Transitions = new Transitions { new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Slide, Easing = T.Ease }, new DoubleTransition { Property = Visual.OpacityProperty, Duration = T.Fast } };
                if (!Shell.Motion.Lite) _hl.Transitions.Add(new DoubleTransition { Property = Layoutable.WidthProperty, Duration = Slide, Easing = T.Ease });
            }
        }

        private static void Glide(Visual v) => v.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Slide, Easing = T.Ease },
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = T.Fast },
        };

        /// <summary>Create the cells (and channel labels) the window needs; drop those neither this nor the last window used.</summary>
        private void Realise(DateTimeOffset winStart, int n)
        {
            var from = winStart - Margin2;
            var to = winStart + Span + Margin2;
            var r0 = Math.Max(0, _firstCh - 1);
            var r1 = Math.Min(n, _firstCh + Visible + 2);
            var wantCells = new HashSet<string>();
            var wantRows = new HashSet<int>();
            for (var row = r0; row < r1; row++)
            {
                wantRows.Add(row);
                if (!_chLabels.ContainsKey(row)) AddLabel(row);
                foreach (var prog in _model!.Channels[row].Schedule)
                {
                    if (prog.End <= from) continue;
                    if (prog.Start >= to) break;
                    var key = $"{row}|{prog.Id}";
                    wantCells.Add(key);
                    if (_cells.ContainsKey(key)) continue;
                    var end = prog.NoInfo ? Min(prog.End, _model.End) : prog.End;
                    var cell = new GuideCell(prog, (end - prog.Start).TotalMinutes * PxPerMin - 8, CellHeight) { ZIndex = 1 };
                    Canvas.SetLeft(cell, X(prog.Start));
                    Canvas.SetTop(cell, Y(row));
                    _cells[key] = cell;
                    _track.Children.Add(cell);
                }
            }
            foreach (var key in _cells.Keys.Where(k => !wantCells.Contains(k) && !_prevCells.Contains(k)).ToList())
            {
                _track.Children.Remove(_cells[key]);
                _cells.Remove(key);
            }
            foreach (var row in _chLabels.Keys.Where(r => !wantRows.Contains(r) && !_prevRows.Contains(r)).ToList())
            {
                _chTrack.Children.Remove(_chLabels[row]);
                _chLabels.Remove(row);
            }
            _prevCells = wantCells;
            _prevRows = wantRows;
        }

        private void AddLabel(int row)
        {
            var ch = _model!.Channels[row].Channel;
            var label = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 18,
                Height = CellHeight,
                Children =
                {
                    new TextBlock { Text = ch.Number, Width = 64, TextAlignment = TextAlignment.Right, FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = T.Text2, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = ch.Name, MaxWidth = TimeStart - T.SafeX - 64 - 18 - 20, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = T.Text, VerticalAlignment = VerticalAlignment.Center },
                },
            };
            Canvas.SetLeft(label, T.SafeX - 40);
            Canvas.SetTop(label, Y(row));
            _chLabels[row] = label;
            _chTrack.Children.Add(label);
        }

        private static ITransform Translate(double x, double y) =>
            TransformOperations.Parse(string.Create(CultureInfo.InvariantCulture, $"translate({x:0.##}px, {y:0.##}px)"));
        private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    }
}
