using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Baird.NewUi.Core;
using Baird.NewUi.Shell;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Controls
{
    /// <summary>
    /// The on-screen keyboard (prototype: overlays/keyboard.js): a digit row, QWERTY letters, then Space / ⌫ / voice.
    /// Arrows move between keys (by horizontal position across rows of different widths); moving off an edge is
    /// reported to the owner, which decides what's there.
    /// </summary>
    public sealed class OnScreenKeyboard : Canvas
    {
        public enum Outcome { Moved, Typed, ExitUp, ExitDown, ExitLeft, ExitRight, Ignored }

        public static readonly string[][] Rows =
        {
            new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
            new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" },
            new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", "'" },
            new[] { "z", "x", "c", "v", "b", "n", "m", "-", "&", "." },
            new[] { "space", "del", "voice" },
        };
        private static readonly Dictionary<string, int> Wide = new() { ["space"] = 5, ["del"] = 3, ["voice"] = 2 };
        private static int WidthOf(string k) => Wide.TryGetValue(k, out var w) ? w : 1;

        public const double KeyH = 62, Gap = 10;
        private readonly double _unit;
        private readonly Border[][] _keys;
        private int _r = 1, _c;
        private bool _active;

        /// <summary>A key was pressed: a character, "space", "del" or "voice".</summary>
        public event Action<string>? Pressed;

        public OnScreenKeyboard(double width)
        {
            Width = width;
            _unit = (width - 9 * Gap) / 10;
            Height = Rows.Length * KeyH + (Rows.Length - 1) * Gap;
            _keys = Rows.Select((row, i) =>
            {
                var x = 0.0;
                return row.Select(k =>
                {
                    var w = WidthOf(k) * _unit + (WidthOf(k) - 1) * Gap;
                    var key = new Border
                    {
                        Width = w,
                        Height = KeyH,
                        CornerRadius = new CornerRadius(14),
                        Background = KeyBack,
                        RenderTransformOrigin = RelativePoint.Center,
                        Child = Label(k),
                        Tag = k,
                    };
                    SetLeft(key, x); SetTop(key, i * (KeyH + Gap));
                    x += w + Gap;
                    Motion.Animate(key, T.Fast);
                    Children.Add(key);
                    return key;
                }).ToArray();
            }).ToArray();
            Draw(false);
        }

        private static readonly IBrush KeyBack = new SolidColorBrush(Color.Parse("#12FFFFFF"));

        private static Control Label(string k) => k switch
        {
            "del" => new Icon(Icon.Delete, 34, T.Text2),
            "voice" => new Icon(Icon.Mic, 30, T.Text2),
            _ => new TextBlock { Text = k == "space" ? "Space" : k.ToUpperInvariant(), FontSize = k == "space" ? 22 : 28, FontWeight = FontWeight.SemiBold, Foreground = T.Text2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };

        public string Current => Rows[_r][_c];
        public Control CurrentKey => _keys[_r][_c];

        public void Draw(bool active)
        {
            _active = active;
            for (var i = 0; i < _keys.Length; i++)
                for (var j = 0; j < _keys[i].Length; j++)
                {
                    var lit = active && i == _r && j == _c;
                    var key = _keys[i][j];
                    key.Classes.Set("focused", lit);
                    key.Background = lit ? T.FocusFill : KeyBack;
                    key.RenderTransform = lit ? Motion.Scale(1.1) : Motion.None;
                    key.BoxShadow = lit ? T.FocusGlow : default;
                    key.ZIndex = lit ? 2 : 0;
                    var ink = lit ? T.FocusInk : T.Text2;
                    if (key.Child is TextBlock tb) tb.Foreground = ink; else if (key.Child is Icon ic) ic.Brush = ink;
                }
        }

        private double XOf(int row, int col) => Rows[row].Take(col).Sum(WidthOf);
        private int ColAt(int row, double x)
        {
            var acc = 0;
            for (var j = 0; j < Rows[row].Length; j++) { acc += WidthOf(Rows[row][j]); if (x < acc) return j; }
            return Rows[row].Length - 1;
        }

        public Outcome OnAction(UiAction a)
        {
            switch (a)
            {
                case UiAction.Left: if (_c == 0) return Outcome.ExitLeft; _c--; break;
                case UiAction.Right: if (_c == Rows[_r].Length - 1) return Outcome.ExitRight; _c++; break;
                case UiAction.Up: { if (_r == 0) return Outcome.ExitUp; var x = XOf(_r, _c); _r--; _c = ColAt(_r, x); break; }
                case UiAction.Down: { if (_r == Rows.Length - 1) return Outcome.ExitDown; var x = XOf(_r, _c); _r++; _c = ColAt(_r, x); break; }
                case UiAction.Ok: Pressed?.Invoke(Current); Draw(_active); return Outcome.Typed;
                default: return Outcome.Ignored;
            }
            Draw(true);
            return Outcome.Moved;
        }
    }
}
