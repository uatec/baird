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
    public sealed record GuideAction(string Label, string? Detail, Action Run, bool Enabled = true);

    /// <summary>
    /// Hold OK in the guide: a glass panel on the right listing what you can do with the lit programme (or, after "All
    /// showings", its showings). ▴ ▾ move, OK runs, Back closes. It lives inside the guide, over the grid.
    /// </summary>
    public sealed class GuideActions : Border
    {
        private const int MaxRows = 8;
        private readonly TextBlock _title = new() { FontSize = 34, FontWeight = FontWeight.Bold, Foreground = T.Text, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _subtitle = new() { FontSize = 23, Foreground = T.Text2, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly StackPanel _list = new() { Spacing = 6 };
        private readonly Panel _listClip = new() { ClipToBounds = true, MaxHeight = MaxRows * 86 };
        private List<GuideAction> _items = new();
        private List<ListRow> _rows = new();
        private int _index;

        public bool IsOpen { get; private set; }
        public IReadOnlyList<string> Labels => _items.Select(i => i.Label).ToList();

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

        public void Open(string title, string subtitle, IReadOnlyList<GuideAction> items)
        {
            _title.Text = title;
            _subtitle.Text = subtitle;
            _items = items.ToList();
            _rows = _items.Select(i => new ListRow(i.Label, i.Detail) { Opacity = i.Enabled ? 1 : 0.4 }).ToList();
            _list.Children.Clear();
            foreach (var r in _rows) _list.Children.Add(r);
            _index = Math.Max(0, _items.FindIndex(i => i.Enabled));
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

        public Control? Lit => IsOpen && _rows.Count > 0 ? _rows[_index] : null;

        public bool OnAction(UiAction a)
        {
            switch (a)
            {
                case UiAction.Back: Close(); return true;
                case UiAction.Up or UiAction.Down: Step(a == UiAction.Up ? -1 : 1); return true;
                case UiAction.Ok:
                    if (_items.Count > 0 && _items[_index].Enabled) _items[_index].Run();
                    return true;
            }
            return true; // the panel is modal: nothing else reaches the grid while it's up
        }

        public void Run(string label) => _items.First(i => i.Label == label).Run();

        private void Step(int d)
        {
            for (var j = _index + d; j >= 0 && j < _items.Count; j += d)
                if (_items[j].Enabled) { _index = j; break; }
            Draw();
        }

        private void Draw()
        {
            for (var i = 0; i < _rows.Count; i++) _rows[i].Lit = i == _index;
            _list.RenderTransform = TransformOperations.Parse($"translate(0px, {-Math.Max(0, _index - (MaxRows - 2)) * 86}px)");
        }
    }
}
