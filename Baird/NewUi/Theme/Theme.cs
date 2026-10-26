using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using System;

namespace Baird.NewUi.Theme
{
    /// <summary>Design tokens, from the prototype (spikes/ux-redesign-prototype/styles/tokens.css).</summary>
    public static class T
    {
        public static readonly FontFamily Font = new("avares://Baird/Assets/Fonts#Instrument Sans");

        public static readonly Color Ground = Colors.Black;
        public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#F5F5F7"));
        public static readonly IBrush Text2 = new SolidColorBrush(Color.Parse("#9EF5F5F7"));   // 62%
        public static readonly IBrush Text3 = new SolidColorBrush(Color.Parse("#61F5F5F7"));   // 38%
        public static readonly IBrush Live = new SolidColorBrush(Color.Parse("#FF3B30"));
        public static readonly IBrush Mine = new SolidColorBrush(Color.Parse("#FFCC66"));
        public static readonly IBrush FocusFill = new SolidColorBrush(Color.Parse("#F5F5F7"));
        public static readonly IBrush FocusInk = new SolidColorBrush(Color.Parse("#0A0A0C"));
        public static readonly IBrush Pill = new SolidColorBrush(Color.Parse("#29FFFFFF"));
        public static readonly IBrush CardBack = new SolidColorBrush(Color.Parse("#1A1A1E"));
        /// <summary>Panels over video. There's no live blur of the picture (mpv/Chromium draw it), so glass is a tint.</summary>
        public static readonly IBrush Glass = new SolidColorBrush(Color.Parse("#E81C1C20"));

        public const double SafeX = 96, SafeTop = 64, SafeBottom = 1030;
        public const double Hero = 112, Title = 64, H2 = 40, H3 = 30, Body = 26, Small = 22;
        public const double RCard = 14, RPanel = 28;

        public static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(220);
        public static readonly TimeSpan Med = TimeSpan.FromMilliseconds(420);
        public static readonly TimeSpan Slow = TimeSpan.FromMilliseconds(600);
        public static readonly Easing Ease = new SplineEasing(0.25, 0.1, 0.25, 1);

        public const double FocusLift = 1.12;
        public static readonly BoxShadows FocusGlow = BoxShadows.Parse("0 0 40 0 #47FFFFFF, 0 34 70 0 #BF000000");
        public static readonly BoxShadows CardShadow = BoxShadows.Parse("0 10 24 0 #73000000");
    }
}
