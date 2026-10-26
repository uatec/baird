using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Baird.NewUi.Model;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// One programme in the guide. Lit = dark ink over the sliding highlight (the cell keeps its size); past = dimmed;
    /// no information = italic and faint; a gold bell when a reminder is set. A title that began before the visible
    /// window is pinned to the window edge (the left in rows, the top in columns, where titles wrap to two lines).
    /// </summary>
    public sealed class GuideCell : Border
    {
        private static readonly IBrush Normal = new SolidColorBrush(Color.Parse("#13FFFFFF"));   // .075
        private static readonly IBrush PastBack = new SolidColorBrush(Color.Parse("#08FFFFFF"));  // .03
        private static readonly IBrush NoInfoBack = new SolidColorBrush(Color.Parse("#0CFFFFFF"));
        private static readonly IBrush InkSoft = new SolidColorBrush(Color.Parse("#990A0A0C"));
        /// <summary>A bell (24×24 box), drawn so it needs no icon font.</summary>
        public static readonly Geometry BellShape = Geometry.Parse("M12,2 C8.7,2 6,4.7 6,8 L6,12.5 L4,16 L20,16 L18,12.5 L18,8 C18,4.7 15.3,2 12,2 Z M9.5,18 C9.5,19.4 10.6,20.5 12,20.5 C13.4,20.5 14.5,19.4 14.5,18 Z");

        public Programme Programme { get; }
        private readonly bool _columns;
        private readonly TextBlock _title;
        private readonly TextBlock? _time;
        private readonly Avalonia.Controls.Shapes.Path _bell = new() { Data = BellShape, Fill = T.Mine, Width = 20, Height = 20, Stretch = Stretch.Uniform, IsVisible = false, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        private bool _lit, _past;

        public GuideCell(Programme p, double width, double height, bool columns = false)
        {
            Programme = p;
            _columns = columns;
            Width = Math.Max(4, width);
            Height = Math.Max(4, height);
            CornerRadius = new CornerRadius(12);
            ClipToBounds = true;
            _title = new TextBlock
            {
                Text = p.Title,
                FontSize = 24,
                FontWeight = FontWeight.SemiBold,
                FontStyle = p.NoInfo ? FontStyle.Italic : FontStyle.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = columns ? TextWrapping.Wrap : TextWrapping.NoWrap,
                MaxLines = columns ? 2 : 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var titleLine = new DockPanel { Children = { _bell, _title } };
            DockPanel.SetDock(_bell, Dock.Left);
            var stack = new StackPanel { VerticalAlignment = columns ? VerticalAlignment.Top : VerticalAlignment.Center, Spacing = 2, Children = { titleLine } };
            if (!p.NoInfo)
            {
                _time = new TextBlock { Text = p.Start.ToLocalTime().ToString("HH:mm"), FontSize = 19 };
                stack.Children.Add(_time);
            }
            Child = stack;
            Inset = 0;
            Apply();
        }

        public bool Lit { get => _lit; set { if (_lit == value) return; _lit = value; Classes.Set("focused", value); Apply(); } }
        public bool Past { set { if (_past == value) return; _past = value; Apply(); } }
        public bool Bell { get => _bell.IsVisible; set => _bell.IsVisible = value; }

        /// <summary>How far the window's edge cuts into this cell, so its title stays in view.</summary>
        public double Inset
        {
            set => Padding = _columns
                ? new Thickness(16, 10 + Math.Max(0, value), 16, 10)
                : new Thickness(18 + Math.Max(0, value), 0, 12, 0);
        }

        private void Apply()
        {
            Background = _lit ? Brushes.Transparent : Programme.NoInfo ? NoInfoBack : _past ? PastBack : Normal;
            _title.Foreground = _lit ? T.FocusInk : Programme.NoInfo || _past ? T.Text3 : T.Text;
            if (_time != null) _time.Foreground = _lit ? InkSoft : T.Text3;
        }
    }
}
