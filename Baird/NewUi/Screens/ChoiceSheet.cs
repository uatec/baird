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
    public sealed record Choice(string Label, string? Hint, Action Run, bool Enabled = true);

    /// <summary>What an actions sheet offers: a title, a line under it, the choices.</summary>
    public sealed record ChoiceList(string Title, string? Subtitle, IReadOnlyList<Choice> Choices);

    /// <summary>
    /// A sheet of choices over the page (prototype: overlays/actions.js), e.g. "Request…": this season, all missing
    /// seasons. ▴ ▾ move, OK chooses (the sheet closes first), Back closes.
    /// </summary>
    public sealed class ChoiceSheet : IScreen
    {
        private readonly NewUiContext _ctx;
        private readonly ChoiceList _list;
        private readonly List<ListRow> _rows;
        private int _index;
        private readonly Panel _root = new() { Width = 1920, Height = 1080, Background = new SolidColorBrush(Color.Parse("#99000000")) };

        public Control View => _root;
        public Control? LitElement => _rows.Count > 0 ? _rows[_index] : null;
        public int Index => _index;

        public ChoiceSheet(NewUiContext ctx, ChoiceList list)
        {
            _ctx = ctx; _list = list;
            _rows = list.Choices.Select(c => new ListRow(c.Label, c.Hint) { Opacity = c.Enabled ? 1 : 0.45 }).ToList();
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock { Text = list.Title, FontSize = T.H2, FontWeight = FontWeight.Bold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap });
            if (list.Subtitle != null) stack.Children.Add(new TextBlock { Text = list.Subtitle, FontSize = T.Small, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 22) });
            foreach (var r in _rows) stack.Children.Add(r);
            _root.Children.Add(new Border
            {
                Background = T.Glass,
                CornerRadius = new CornerRadius(T.RPanel),
                Padding = new Thickness(40, 36),
                Width = 900,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = stack,
            });
            Draw();
        }

        private void Draw() { for (var i = 0; i < _rows.Count; i++) _rows[i].Lit = i == _index; }

        public bool OnAction(UiAction a, bool repeat)
        {
            switch (a)
            {
                case UiAction.Up or UiAction.Down when _rows.Count > 0:
                    _index = Math.Clamp(_index + (a == UiAction.Up ? -1 : 1), 0, _rows.Count - 1);
                    Draw();
                    return true;
                case UiAction.Ok or UiAction.PlayPause when _rows.Count > 0:
                    var c = _list.Choices[_index];
                    if (!c.Enabled) return true;
                    _ctx.Nav.CloseOverlay();
                    c.Run();
                    return true;
                case UiAction.Back: return false; // the shell closes the sheet
            }
            return true; // nothing else leaks through to the page beneath
        }
    }
}
