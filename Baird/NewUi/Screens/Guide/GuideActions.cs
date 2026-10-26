using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Baird.NewUi.Core;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    public sealed record GuideButton(string Label, Action Run);

    /// <summary>
    /// A row in the guide's panel. With <paramref name="Buttons"/> (All showings), ◂ ▸ choose between them and OK runs
    /// the chosen one; otherwise OK runs <paramref name="Run"/>. <paramref name="Pills"/> are small labels (New, Repeat).
    /// </summary>
    public sealed record GuideAction(string Label, string? Detail, Action Run, bool Enabled = true,
        IReadOnlyList<string>? Pills = null, IReadOnlyList<GuideButton>? Buttons = null);

    /// <summary>
    /// Hold OK in the guide: a glass panel on the right listing what you can do with the lit programme (or, after "All
    /// showings", its showings, each with its own buttons). ▴ ▾ move, ◂ ▸ choose a button, OK runs, Back closes. It
    /// lives inside the guide, over the grid.
    /// </summary>
    public sealed class GuideActions : Border
    {
        private const int MaxRows = 8;
        private const double Step0 = 86;
        private readonly TextBlock _title = new() { FontSize = 34, FontWeight = FontWeight.Bold, Foreground = T.Text, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _subtitle = new() { FontSize = 23, Foreground = T.Text2, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly StackPanel _list = new() { Spacing = 6 };
        private readonly Panel _listClip = new() { ClipToBounds = true, MaxHeight = MaxRows * Step0 };
        private List<GuideAction> _items = new();
        private List<Control> _rows = new();
        private int _index, _col;

        public bool IsOpen { get; private set; }
        public IReadOnlyList<string> Labels => _items.Select(i => i.Label).ToList();
        public IReadOnlyList<GuideAction> Items => _items;
        public int Index => _index;

        public GuideActions()
        {
            Width = 700;
            HorizontalAlignment = HorizontalAlignment.Right;
            VerticalAlignment = VerticalAlignment.Top;
            Margin = new Thickness(0, 150, T.SafeX, 0);
            Padding = new Thickness(26, 30);
            CornerRadius = new CornerRadius(T.RPanel);
            Background = T.Glass;
            BoxShadow = BoxShadows.Parse("0 30 80 0 #CC000000");
            IsHitTestVisible = false;
            _listClip.Children.Add(_list);
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { _title, _subtitle, new Border { Height = 18 }, _listClip },
            };
            Opacity = 0;
            IsVisible = false;
            Motion.Animate(_list, T.Fast);
        }

        /// <summary>Show <paramref name="items"/>. <paramref name="keep"/> keeps the lit row and button (a row's own action redrew it).</summary>
        public void Open(string title, string subtitle, IReadOnlyList<GuideAction> items, bool keep = false)
        {
            _title.Text = title;
            _subtitle.Text = subtitle;
            _items = items.ToList();
            Width = _items.Any(i => i.Buttons is { Count: > 0 }) ? 900 : 700;
            _rows = _items.Select(i => i.Buttons is { Count: > 0 } ? (Control)new ShowingRow(i) : new ListRow(i.Label, i.Detail) { Opacity = i.Enabled ? 1 : 0.4 }).ToList();
            _list.Children.Clear();
            foreach (var r in _rows) _list.Children.Add(r);
            if (keep && _index < _items.Count) _col = Math.Min(_col, Math.Max(0, (_items[_index].Buttons?.Count ?? 1) - 1));
            else { _index = Math.Max(0, _items.FindIndex(i => i.Enabled)); _col = 0; }
            if (!IsOpen)
            {
                IsOpen = true;
                IsVisible = true;
                Motion.Enter(this, 40, 0);
            }
            Draw();
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            Motion.Exit(this, 40, 0, () => { if (!IsOpen) IsVisible = false; });
        }

        public Control? Lit => !IsOpen || _rows.Count == 0 ? null : _rows[_index] is ShowingRow s ? s.Button(_col) : _rows[_index];

        public bool OnAction(UiAction a)
        {
            switch (a)
            {
                case UiAction.Back: Close(); return true;
                case UiAction.Up or UiAction.Down: Step(a == UiAction.Up ? -1 : 1); return true;
                case UiAction.Left or UiAction.Right when _items.Count > 0 && _items[_index].Buttons is { Count: > 0 } b:
                    _col = Math.Clamp(_col + (a == UiAction.Left ? -1 : 1), 0, b.Count - 1);
                    Draw();
                    return true;
                case UiAction.Ok:
                    if (_items.Count == 0 || !_items[_index].Enabled) return true;
                    if (_items[_index].Buttons is { Count: > 0 } buttons) buttons[_col].Run();
                    else _items[_index].Run();
                    return true;
            }
            return true; // the panel is modal: nothing else reaches the grid while it's up
        }

        public void Run(string label) => _items.First(i => i.Label == label).Run();

        private void Step(int d)
        {
            for (var j = _index + d; j >= 0 && j < _items.Count; j += d)
                if (_items[j].Enabled) { _index = j; _col = 0; break; }
            Draw();
        }

        private void Draw()
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                if (_rows[i] is ShowingRow s) s.Light(i == _index ? _col : null);
                else if (_rows[i] is ListRow l) l.Lit = i == _index;
            }
            _list.RenderTransform = TransformOperations.Parse($"translate(0px, {-Math.Max(0, _index - (MaxRows - 2)) * Step0}px)");
        }

        /// <summary>A showing: when and where, New / Repeat / Live pills, the detail, and its buttons on the right.</summary>
        private sealed class ShowingRow : Border
        {
            private static readonly IBrush RowLit = new SolidColorBrush(Color.Parse("#1FFFFFFF"));
            private readonly List<Border> _buttons = new();

            public ShowingRow(GuideAction a)
            {
                Height = 80; CornerRadius = new CornerRadius(18); Padding = new Thickness(24, 0, 14, 0);
                var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                head.Children.Add(new TextBlock { Text = a.Label, FontSize = 25, FontWeight = FontWeight.SemiBold, Foreground = T.Text, VerticalAlignment = VerticalAlignment.Center });
                foreach (var p in a.Pills ?? Array.Empty<string>())
                    head.Children.Add(new Border
                    {
                        Background = p == "Live" ? T.Live : T.Pill,
                        CornerRadius = new CornerRadius(999),
                        Padding = new Thickness(10, 2),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = p, FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = p == "New" ? T.Mine : T.Text },
                    });
                var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { head } };
                if (!string.IsNullOrEmpty(a.Detail)) left.Children.Add(new TextBlock { Text = a.Detail, FontSize = 20, Foreground = T.Text2, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 440 });
                var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
                foreach (var b in a.Buttons!)
                {
                    var btn = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(18, 8), Child = new TextBlock { Text = b.Label, FontSize = 20, FontWeight = FontWeight.SemiBold } };
                    _buttons.Add(btn);
                    right.Children.Add(btn);
                }
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                Grid.SetColumn(right, 1);
                grid.Children.Add(left);
                grid.Children.Add(right);
                Child = grid;
                Light(null);
            }

            public Control Button(int i) => _buttons[Math.Clamp(i, 0, _buttons.Count - 1)];

            /// <summary>Light this row with button <paramref name="col"/>, or unlight it (null).</summary>
            public void Light(int? col)
            {
                Background = col != null ? RowLit : Brushes.Transparent;
                for (var i = 0; i < _buttons.Count; i++)
                {
                    var on = col == i;
                    _buttons[i].Background = on ? T.FocusFill : T.Pill;
                    _buttons[i].Classes.Set("focused", on);
                    ((TextBlock)_buttons[i].Child!).Foreground = on ? T.FocusInk : T.Text;
                }
            }
        }
    }
}
