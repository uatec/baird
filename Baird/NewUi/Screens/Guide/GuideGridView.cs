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
    /// Where the guide's grid sits (prototype GEOM). Rows: channels down from y = ChStart, time across from
    /// x = TimeStart. Columns: channels across from x = ChStart, time down from y = TimeStart.
    /// </summary>
    public sealed record GuideGeometry(GuideLayout Layout, double ChStart, double ChSize, int Visible, double TimeStart, double PxPerMin, TimeSpan Span)
    {
        public static readonly GuideGeometry Rows = new(GuideLayout.Rows, 548, 84, 6, 376, 12, TimeSpan.FromMinutes(120));
        public static readonly GuideGeometry Columns = new(GuideLayout.Columns, 200, 344, 5, 560, 4.4, TimeSpan.FromMinutes(118));
        public static GuideGeometry For(GuideLayout l) => l == GuideLayout.Columns ? Columns : Rows;
        public bool IsColumns => Layout == GuideLayout.Columns;
    }

    /// <summary>
    /// The guide's grid: channels on one axis, time on the other, a red now line, and a highlight that slides from cell
    /// to cell. The grid scrolls only when focus nears an edge. Only the cells in (and just around) the visible window
    /// exist: a channel either side, an hour either side, plus whatever the previous window showed while it slides away.
    /// </summary>
    public sealed class GuideGridView : Panel
    {
        private static readonly TimeSpan Margin2 = TimeSpan.FromMinutes(60);
        private static readonly TimeSpan Half = TimeSpan.FromMinutes(30);

        private readonly GuideGeometry _g;
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

        public GuideGeometry Geometry => _g;
        public DateTimeOffset WinStart => _winStart ?? _model?.Start ?? default;
        public int FirstChannel => _firstCh;
        public int RealisedCells => _cells.Count;
        public GuideCell? LitCell { get; private set; }
        public IEnumerable<GuideCell> Cells => _cells.Values;
        public Control Highlight => _hl;

        private static TimeSpan Slide => Shell.Motion.Lite ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromMilliseconds(460);

        public GuideGridView(GuideGeometry? geometry = null)
        {
            _g = geometry ?? GuideGeometry.Rows;
            Width = 1920; Height = 1080;
            IsHitTestVisible = false;

            _hl = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = T.FocusFill,
                BoxShadow = BoxShadows.Parse("0 0 0 3 #99FFFFFF, 0 0 34 0 #40FFFFFF, 0 16 40 0 #99000000"),
                ZIndex = 0,
            };
            _track.Children.Add(_hl);

            var line = new Border { Background = T.Live, BoxShadow = BoxShadows.Parse("0 0 16 0 #B3FF3B30") };
            var dot = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(7), Background = T.Live };
            if (_g.IsColumns)
            {
                _now = new Panel { Height = 13, Width = 1920 - _g.ChStart + 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(_g.ChStart - 6, -6, 0, 0), Opacity = 0 };
                line.Height = 3; line.VerticalAlignment = VerticalAlignment.Center;
                dot.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
            {
                _now = new Panel { Width = 13, Height = 1080 - _g.ChStart + 28, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-5, _g.ChStart - 28, 0, 0), Opacity = 0 };
                line.Width = 3; line.HorizontalAlignment = HorizontalAlignment.Center;
                dot.VerticalAlignment = VerticalAlignment.Top; dot.Margin = new Thickness(0, -6, 0, 0);
            }
            _now.Children.Add(line);
            _now.Children.Add(dot);

            if (_g.IsColumns)
            {
                Children.Add(Clip(0, _g.TimeStart - 14, 180, 1080 - _g.TimeStart + 14, _rulerTrack));
                Children.Add(Clip(_g.ChStart, 500, 1920 - _g.ChStart, 52, _chTrack));
                Children.Add(Clip(_g.ChStart, _g.TimeStart, 1920 - _g.ChStart, 1080 - _g.TimeStart, _track));
            }
            else
            {
                Children.Add(Clip(_g.TimeStart, 506, 1920 - _g.TimeStart, 36, _rulerTrack));
                Children.Add(Clip(0, _g.ChStart, _g.TimeStart, 1080 - _g.ChStart, _chTrack));
                Children.Add(Clip(_g.TimeStart, _g.ChStart, 1920 - _g.TimeStart, 1080 - _g.ChStart, _track));
            }
            Children.Add(_now);
            Children.Add(new Border
            {
                Height = 90,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new LinearGradientBrush { StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative), GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) } },
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

        /// <summary>Time → px along the time axis; channel → px along the channel axis.</summary>
        private double Tp(DateTimeOffset t) => (t - _model!.Start).TotalMinutes * _g.PxPerMin;
        private double Cp(int ch) => ch * _g.ChSize;

        /// <summary>Put a cell-shaped element at (channel, a → b), inset so neighbours have a gap.</summary>
        private (double X, double Y, double W, double H) Box(int ch, DateTimeOffset a, DateTimeOffset b)
        {
            var len = Math.Max(4, (b - a).TotalMinutes * _g.PxPerMin - 8);
            return _g.IsColumns ? (Cp(ch), Tp(a), _g.ChSize - 10, len) : (Tp(a), Cp(ch), len, _g.ChSize - 8);
        }

        private ITransform Along(double timeOff, double chOff) => _g.IsColumns ? Translate(chOff, timeOff) : Translate(timeOff, chOff);

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
                if (_g.IsColumns) { label.Width = 156; label.TextAlignment = TextAlignment.Right; Canvas.SetTop(label, Tp(t)); }
                else Canvas.SetLeft(label, Tp(t));
                _rulerTrack.Children.Add(label);
            }
            _winStart = null;
            _firstCh = 0;
        }

        /// <summary>Lay the grid out around the lit programme. <paramref name="focused"/> = false while focus is elsewhere.</summary>
        public void Draw(GuidePos pos, Programme p, DateTimeOffset now, bool focused, Func<string, bool>? reminded = null)
        {
            if (_model == null || _model.Channels.Count == 0) return;
            var n = _model.Channels.Count;

            // Time window: move only when focus nears an edge, keeping half an hour of context.
            var focusT = p.IsOnAt(now) ? Max(p.Start, now - Half) : Max(p.Start, _model.Start);
            var ws = _winStart ?? _model.Start;
            if (_winStart == null || focusT < ws + TimeSpan.FromMinutes(10) || focusT > ws + _g.Span - TimeSpan.FromMinutes(40))
            {
                var t = focusT - Half;
                var floored = t - TimeSpan.FromTicks((t - _model.Start).Ticks % Half.Ticks);
                _winStart = Max(_model.Start, floored);
            }
            var winStart = _winStart!.Value;
            // Channel window: move only when focus is within one channel of the edge.
            if (pos.Ch < _firstCh + 1) _firstCh = Math.Max(0, pos.Ch - 1);
            if (pos.Ch > _firstCh + _g.Visible - 2) _firstCh = pos.Ch - (_g.Visible - 2);
            _firstCh = Math.Clamp(_firstCh, 0, Math.Max(0, n - _g.Visible));

            Realise(winStart, n);

            var tOff = -Tp(winStart);
            var cOff = -Cp(_firstCh);
            _track.RenderTransform = Along(tOff, cOff);
            _rulerTrack.RenderTransform = Along(tOff, 0);
            _chTrack.RenderTransform = Along(0, cOff);
            _now.RenderTransform = Along(_g.TimeStart + Tp(now) + tOff, 0);
            _now.Opacity = now > winStart && now < winStart + _g.Span ? 1 : 0;

            // The highlight slides to the lit programme, clipped to the window so long programmes still read.
            var a = Max(p.Start, winStart);
            var b = p.NoInfo ? winStart + _g.Span : Min(p.End, winStart + _g.Span);
            if (b < a + TimeSpan.FromMinutes(15)) b = a + TimeSpan.FromMinutes(15);
            var box = Box(pos.Ch, a, b);
            _hl.RenderTransform = Translate(box.X, box.Y);
            _hl.Width = box.W;
            _hl.Height = box.H;
            _hl.Opacity = focused ? 1 : 0;

            LitCell = null;
            foreach (var cell in _cells.Values)
            {
                var lit = focused && cell.Programme.Id == p.Id && cell.Programme.ChannelId == p.ChannelId;
                cell.Lit = lit;
                if (lit) LitCell = cell;
                cell.Past = cell.Programme.End <= now;
                cell.Bell = reminded != null && !cell.Programme.NoInfo && reminded(cell.Programme.Id);
                cell.Inset = cell.Programme.Start < winStart && cell.Programme.End > winStart ? (winStart - cell.Programme.Start).TotalMinutes * _g.PxPerMin : 0;
            }
            foreach (var (row, label) in _chLabels) label.Opacity = row == pos.Ch ? 1 : 0.62;

            if (!_animating)
            {
                // From the first layout on, movement glides (the first placement doesn't).
                _animating = true;
                foreach (var v in new Visual[] { _track, _rulerTrack, _chTrack, _now }) Glide(v);
                _hl.Transitions = new Transitions { new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = Slide, Easing = T.Ease }, new DoubleTransition { Property = Visual.OpacityProperty, Duration = T.Fast } };
                if (!Shell.Motion.Lite) _hl.Transitions.Add(new DoubleTransition { Property = _g.IsColumns ? Layoutable.HeightProperty : Layoutable.WidthProperty, Duration = Slide, Easing = T.Ease });
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
            var to = winStart + _g.Span + Margin2;
            var r0 = Math.Max(0, _firstCh - 1);
            var r1 = Math.Min(n, _firstCh + _g.Visible + 2);
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
                    var box = Box(row, prog.Start, end);
                    var cell = new GuideCell(prog, box.W, box.H, _g.IsColumns) { ZIndex = 1 };
                    Canvas.SetLeft(cell, box.X);
                    Canvas.SetTop(cell, box.Y);
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
            var number = new TextBlock { Text = ch.Number, FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = T.Text2, VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = ch.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = T.Text, VerticalAlignment = VerticalAlignment.Center };
            var label = new StackPanel { Orientation = Orientation.Horizontal, Children = { number, name } };
            if (_g.IsColumns)
            {
                label.Spacing = 12; label.Height = 52; label.Width = _g.ChSize - 10;
                name.MaxWidth = _g.ChSize - 10 - 80;
                Canvas.SetLeft(label, Cp(row));
            }
            else
            {
                label.Spacing = 18; label.Height = _g.ChSize - 8;
                number.Width = 64; number.TextAlignment = TextAlignment.Right;
                name.MaxWidth = _g.TimeStart - T.SafeX - 64 - 18 - 20;
                Canvas.SetLeft(label, T.SafeX - 40);
                Canvas.SetTop(label, Cp(row));
            }
            _chLabels[row] = label;
            _chTrack.Children.Add(label);
        }

        private static ITransform Translate(double x, double y) =>
            TransformOperations.Parse(string.Create(CultureInfo.InvariantCulture, $"translate({x:0.##}px, {y:0.##}px)"));
        private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    }
}
