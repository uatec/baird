using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Baird.NewUi.Core;
using Baird.NewUi.Model;
using Baird.NewUi.Theme;

namespace Baird.NewUi.Screens
{
    /// <summary>
    /// A reminder, a minute before (prototype js/overlays/reminder.js): what, where, a countdown, and the choice —
    /// Switch now / Dismiss (◂ ▸, OK; Back dismisses). It switches at the start unless dismissed: never without warning.
    /// An overlay, so it sits over whatever is showing, the video included.
    /// </summary>
    public sealed class ReminderCard : IScreen
    {
        private static readonly TimeSpan TickEvery = TimeSpan.FromMilliseconds(500);
        private readonly NewUiContext _ctx;
        private readonly Reminder _r;
        private readonly Panel _root = new() { Width = 1920, Height = 1080 };
        private readonly TextBlock _eyebrow = new() { FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = T.Text2 };
        private readonly TextBlock _title = new() { FontSize = 40, FontWeight = FontWeight.Bold, Foreground = T.Text, LetterSpacing = -0.8, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new Thickness(0, 10, 0, 8) };
        private readonly TextBlock _then = new() { FontSize = 24, Foreground = T.Text2, TextWrapping = TextWrapping.Wrap };
        private readonly Border[] _buttons = new Border[2];
        private int _focus;
        private IDisposable? _tick;
        private bool _done;

        public Control View => _root;
        public Control? LitElement => _buttons[_focus];
        /// <summary>Everything the card says (tests).</summary>
        public string Text => $"{_eyebrow.Text}\n{_title.Text}\n{_then.Text}";

        public ReminderCard(NewUiContext ctx, Reminder r)
        {
            _ctx = ctx; _r = r;
            _title.Text = r.Title;
            _then.Text = $"{r.Start.ToLocalTime():HH:mm} on {r.ChannelNumber} {r.ChannelName}. We'll switch over when it starts.";
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 24, 0, 0) };
            for (var i = 0; i < 2; i++)
            {
                _buttons[i] = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(26, 12), Child = new TextBlock { Text = i == 0 ? "Switch now" : "Dismiss", FontSize = 24, FontWeight = FontWeight.SemiBold } };
                btns.Children.Add(_buttons[i]);
            }
            var hue = 0; foreach (var c in r.ChannelId) hue = (hue * 31 + c) % 360;
            var thumb = new Border
            {
                Width = 280,
                Height = 158,
                CornerRadius = new CornerRadius(14),
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Top,
                Background = new LinearGradientBrush { StartPoint = new RelativePoint(0.8, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.2, 1, RelativeUnit.Relative), GradientStops = { new GradientStop(HsvColor.FromAhsv(1, hue, 0.55, 0.36).ToRgb(), 0), new GradientStop(HsvColor.FromAhsv(1, hue, 0.6, 0.1).ToRgb(), 1) } },
                Child = new TextBlock { Text = r.Title, FontSize = 22, FontWeight = FontWeight.Bold, Foreground = T.Text, TextWrapping = TextWrapping.Wrap, MaxLines = 3, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(16) },
            };
            var text = new StackPanel { Width = 860 - 52 - 280 - 26, Children = { _eyebrow, _title, _then, btns } };
            _root.Children.Add(new Border
            {
                Width = 860,
                Padding = new Thickness(26),
                CornerRadius = new CornerRadius(26),
                Background = T.Glass,
                BoxShadow = BoxShadows.Parse("0 30 80 0 #CC000000"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 64, 64, 0),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 26, Children = { thumb, text } },
            });
            Draw();
            _tick = _ctx.Timers.After(TickEvery, Tick);
        }

        private void Tick()
        {
            if (_done) return;
            if (_ctx.Now() >= _r.Start) { SwitchNow(); return; }
            Draw();
            _tick = _ctx.Timers.After(TickEvery, Tick);
        }

        private void Draw()
        {
            var s = Math.Max(0, (int)Math.Round((_r.Start - _ctx.Now()).TotalSeconds));
            _eyebrow.Text = $"Reminder · starts in {s / 60}:{s % 60:00}";
            for (var i = 0; i < 2; i++)
            {
                _buttons[i].Background = i == _focus ? T.FocusFill : T.Pill;
                ((TextBlock)_buttons[i].Child!).Foreground = i == _focus ? T.FocusInk : T.Text;
            }
        }

        public bool OnAction(UiAction a, bool repeat)
        {
            switch (a)
            {
                case UiAction.Left or UiAction.Right: _focus = a == UiAction.Left ? 0 : 1; Draw(); return true;
                case UiAction.Ok: if (_focus == 0) SwitchNow(); else Dismiss(); return true;
                case UiAction.Back: Dismiss(); return true;
                case UiAction.Up or UiAction.Down: return true;
            }
            return false;
        }

        private void Finish()
        {
            _done = true;
            _tick?.Dispose();
            _ctx.Reminders.Remove(_r.ProgrammeId);
            if (_ctx.Nav.State.Overlay?.Name == "reminder") _ctx.Nav.CloseOverlay();
        }

        private void Dismiss()
        {
            Finish();
            _ctx.Toast($"Dismissed: you'll stay where you are. {_r.Title} starts at {_r.Start.ToLocalTime():HH:mm} on {_r.ChannelName}.");
        }

        private async void SwitchNow()
        {
            Finish();
            var ch = _ctx.Channels.FirstOrDefault(c => c.Id == _r.ChannelId);
            if (ch == null)
            {
                try { ch = (await _ctx.Home.ChannelsAsync()).FirstOrDefault(c => c.Id == _r.ChannelId); }
                catch (Exception ex) { Console.WriteLine($"[Reminders] Channels failed: {ex.Message}"); }
            }
            if (ch == null) { _ctx.Toast($"{_r.ChannelName} isn't available right now."); return; }
            _ctx.PlayLive(ch);
        }

        public void Destroy() { _done = true; _tick?.Dispose(); }
    }
}
