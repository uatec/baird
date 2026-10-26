using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;
using Baird.Services;

namespace Baird.NewUi.Screens
{
    /// <summary>A lit-able list row (Settings, the On-now list).</summary>
    public sealed class ListRow : Border
    {
        private readonly TextBlock _label, _value;
        public ListRow(string label, string? value)
        {
            Height = 80; CornerRadius = new CornerRadius(18); Padding = new Thickness(28, 0);
            _label = new TextBlock { Text = label, FontSize = 28, FontWeight = FontWeight.SemiBold, Foreground = T.Text, VerticalAlignment = VerticalAlignment.Center };
            _value = new TextBlock { Text = value ?? "", FontSize = 26, Foreground = T.Text2, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Child = new Grid { Children = { _label, _value } };
            Motion.Animate(this, T.Fast);
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        }
        public string Value { set => _value.Text = value; }
        public bool Lit
        {
            set
            {
                Classes.Set("focused", value);
                Background = value ? T.FocusFill : Brushes.Transparent;
                _label.Foreground = value ? T.FocusInk : T.Text;
                _value.Foreground = value ? new SolidColorBrush(Color.Parse("#990A0A0C")) : T.Text2;
                RenderTransform = value ? TransformOperations.Parse("scale(1.02)") : TransformOperations.Parse("none");
                BoxShadow = value ? T.FocusGlow : default;
            }
        }
    }

    /// <summary>A vertical list with a title on the left: Settings, and the Search placeholder.</summary>
    public abstract class ListScreen : IScreen
    {
        protected readonly Panel Root = new() { Width = 1920, Height = 1080, Background = new SolidColorBrush(Color.Parse("#F0000000")) };
        protected readonly StackPanel List = new() { Spacing = 6, Width = 1060, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(760, 150, 0, 0) };
        protected List<ListRow> Rows = new();
        protected int Index;
        private bool _lit = true;
        public Control View => Root;
        public Control? LitElement => _lit && Rows.Count > 0 ? Rows[Index] : null;

        protected ListScreen(string title, string? note)
        {
            var side = new StackPanel { Width = 600, Margin = new Thickness(T.SafeX, 320, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Spacing = 20 };
            side.Children.Add(new TextBlock { Text = title, FontSize = 72, FontWeight = FontWeight.Bold, Foreground = T.Text });
            if (note != null) side.Children.Add(new TextBlock { Text = note, FontSize = 26, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap });
            Root.Children.Add(side);
            Root.Children.Add(List);
            Motion.Animate(List, Motion.Med);
        }

        protected void SetRows(IEnumerable<ListRow> rows)
        {
            Rows = rows.ToList(); List.Children.Clear();
            foreach (var r in Rows) List.Children.Add(r);
            Index = Math.Clamp(Index, 0, Math.Max(0, Rows.Count - 1));
            Draw();
        }

        protected void Draw()
        {
            for (var i = 0; i < Rows.Count; i++) Rows[i].Lit = _lit && i == Index;
            // Keep the lit row on screen.
            List.RenderTransform = TransformOperations.Parse($"translate(0px, {-Math.Max(0, Index - 8) * 86}px)");
        }

        public virtual bool OnAction(UiAction a, bool repeat)
        {
            if (a == UiAction.Up && Index == 0) return false;
            if (a is UiAction.Up or UiAction.Down && Rows.Count > 0) { Index = Math.Clamp(Index + (a == UiAction.Up ? -1 : 1), 0, Rows.Count - 1); Draw(); return true; }
            return false;
        }
        public virtual void OnShow() { }
        public void OnFocusIn() { _lit = true; Draw(); }
        public void OnFocusOut() { _lit = false; Draw(); }
    }

    /// <summary>Search in phase 1: a note. One search for everything arrives in phase 5.</summary>
    public sealed class SearchPlaceholder : ListScreen
    {
        public SearchPlaceholder() : base("Search", "One search across shows, films, channels and what's on arrives in phase 5. Switch to Classic in Settings to search now.") { }
        public override bool OnAction(UiAction a, bool repeat) => a != UiAction.Up && a != UiAction.Back && a != UiAction.Voice || base.OnAction(a, repeat);
    }

    /// <summary>New-mode Settings: Interface (Classic / New, restarts) and Interface size. ◂ ▸ change values.</summary>
    public sealed class SettingsScreen : ListScreen
    {
        private readonly Func<UiMode> _getMode;
        private readonly Action<UiMode> _setModeAndRestart;
        private readonly ListRow _interface, _size;
        private double _scale;
        private readonly Action<double> _setScale;

        public SettingsScreen(Func<UiMode> getMode, Action<UiMode> setModeAndRestart, double scale, Action<double> setScale) : base("Settings", null)
        {
            _getMode = getMode; _setModeAndRestart = setModeAndRestart; _scale = scale; _setScale = setScale;
            _interface = new ListRow("Interface", "New  ·  ◂ ▸ to switch to Classic");
            _size = new ListRow("Interface size", $"‹  {_scale * 100:0}%  ›");
            SetRows(new[] { _interface, _size });
        }

        public override bool OnAction(UiAction a, bool repeat)
        {
            if (Index == 0 && a is UiAction.Left or UiAction.Right or UiAction.Ok)
            {
                _interface.Value = "Switching to Classic… Baird restarts";
                _setModeAndRestart(_getMode() == UiMode.New ? UiMode.Classic : UiMode.New);
                return true;
            }
            if (Index == 1 && a is UiAction.Left or UiAction.Right)
            {
                _scale = Math.Clamp(Math.Round(_scale + (a == UiAction.Left ? -0.1 : 0.1), 1), 0.8, 1.3);
                _size.Value = $"‹  {_scale * 100:0}%  ›";
                _setScale(_scale);
                return true;
            }
            return base.OnAction(a, repeat);
        }
    }

    /// <summary>A page that isn't built yet (none are reachable in phase 1; a safe fallback).</summary>
    public sealed class EmptyScreen : IScreen
    {
        public Control View { get; } = new Border { Width = 1920, Height = 1080, Background = Brushes.Black };
        public bool OnAction(UiAction a, bool repeat) => a != UiAction.Back && a != UiAction.Up;
    }
}
