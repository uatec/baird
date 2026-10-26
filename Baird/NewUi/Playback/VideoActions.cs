using System;
using System.Linq;
using Baird.NewUi.Core;
using Baird.NewUi.Model;

namespace Baird.NewUi.Playback
{
    /// <summary>
    /// Keys while the video is in front: OK shows the info bar, ⏯ pauses, ◂ ▸ and ⏮ ⏭ scan, Pg± change channel,
    /// digits type a channel number. Back is left to the navigator (it returns to where you came from).
    /// </summary>
    public sealed class VideoActions
    {
        public static readonly TimeSpan NumberWait = TimeSpan.FromSeconds(2);
        private readonly NewUiContext _ctx;
        private readonly ITimers _timers;
        private NumberEntry? _entry;
        private IDisposable? _entryTimer;

        public VideoActions(NewUiContext ctx, ITimers timers) { _ctx = ctx; _timers = timers; }

        public bool OnAction(UiAction a, bool repeat)
        {
            var p = _ctx.Playback;
            if (InputMap.DigitOf(a) is { } digit) { Digit(digit); return true; }
            switch (a)
            {
                case UiAction.Ok:
                    if (p.Current == null) return false;
                    _ctx.Nav.OpenOverlay(new OverlayRef("info"));
                    return true;
                case UiAction.PlayPause: p.TogglePause(); return true;
                case UiAction.Left or UiAction.Right:
                    p.SeekRelative(TimeSpan.FromSeconds((a == UiAction.Left ? -1 : 1) * (repeat ? 120 : 30))); return true;
                case UiAction.Prev or UiAction.Next:
                    p.SeekRelative(TimeSpan.FromSeconds(a == UiAction.Prev ? -10 : 30)); return true;
                case UiAction.PageUp or UiAction.PageDown:
                    if (p.Current?.Kind != "live") return false;
                    if (_ctx.NeighbourChannel(a == UiAction.PageUp ? 1 : -1) is { } ch) _ctx.PlayLive(ch);
                    return true;
            }
            return false;
        }

        private void Digit(int d)
        {
            _entry ??= new NumberEntry(_ctx.Channels.Select(c => c.Number));
            var r = _entry.Push(d);
            _entryTimer?.Dispose();
            if (r.Error != null) { _ctx.Toast(r.Error); _entry = null; return; }
            _ctx.Toast(r.Text);
            if (_entry.IsUnique) { Commit(); return; }
            _entryTimer = _timers.After(NumberWait, Commit);
        }

        private void Commit()
        {
            var r = _entry?.State();
            _entry = null;
            if (r?.Match is not { } number) { if (r?.Text is { Length: > 0 } t) _ctx.Toast($"No channel {t}"); return; }
            var ch = _ctx.Channels.FirstOrDefault(c => c.Number == number);
            if (ch != null) _ctx.PlayLive(ch);
        }
    }
}
