using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Baird.NewUi.Model;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// One programme in the guide. Lit = dark ink over the sliding highlight (the cell keeps its size); past = dimmed;
    /// no information = italic and faint. A title that began before the visible window is pinned to the window edge.
    /// </summary>
    public sealed class GuideCell : Border
    {
        private static readonly IBrush Normal = new SolidColorBrush(Color.Parse("#13FFFFFF"));   // .075
        private static readonly IBrush PastBack = new SolidColorBrush(Color.Parse("#08FFFFFF"));  // .03
        private static readonly IBrush NoInfoBack = new SolidColorBrush(Color.Parse("#0CFFFFFF"));
        private static readonly IBrush InkSoft = new SolidColorBrush(Color.Parse("#990A0A0C"));

        public Programme Programme { get; }
        private readonly TextBlock _title;
        private readonly TextBlock? _time;
        private bool _lit, _past;

        public GuideCell(Programme p, double width, double height)
        {
            Programme = p;
            Width = Math.Max(4, width);
            Height = height;
            CornerRadius = new CornerRadius(12);
            ClipToBounds = true;
            _title = new TextBlock
            {
                Text = p.Title,
                FontSize = 24,
                FontWeight = FontWeight.SemiBold,
                FontStyle = p.NoInfo ? FontStyle.Italic : FontStyle.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Children = { _title } };
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

        /// <summary>How far the window's edge cuts into this cell, so its title stays in view.</summary>
        public double Inset { set => Padding = new Thickness(18 + Math.Max(0, value), 0, 12, 0); }

        private void Apply()
        {
            Background = _lit ? Brushes.Transparent : Programme.NoInfo ? NoInfoBack : _past ? PastBack : Normal;
            _title.Foreground = _lit ? T.FocusInk : Programme.NoInfo || _past ? T.Text3 : T.Text;
            if (_time != null) _time.Foreground = _lit ? InkSoft : T.Text3;
        }
    }
}
