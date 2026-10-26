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
        private readonly NewUiContext _ctx;
        private readonly ITimers _timers;

        public VideoActions(NewUiContext ctx, ITimers timers) { _ctx = ctx; _timers = timers; }

        public bool OnAction(UiAction a, bool repeat)
        {
            var p = _ctx.Playback;
            if (InputMap.DigitOf(a) is { } digit)
            {
                if (_ctx.Channels.Count == 0) return false;
                _ctx.Nav.OpenOverlay(new OverlayRef("number", digit));
                return true;
            }
            switch (a)
            {
                case UiAction.Ok:
                    if (p.Current == null) return false;
                    if (_ctx.TryActivatePrompt()) return true;
                    if (p.Failure != null && _ctx.FailureAction() != null) { _ = RunFailureActionAsync(); return true; }
                    _ctx.Nav.OpenOverlay(new OverlayRef("info"));
                    return true;
                case UiAction.PlayPause: p.TogglePause(); ShowInfo(); return true;
                case UiAction.HoldOk:
                    if (p.Current == null) return false;
                    _ctx.Nav.OpenOverlay(new OverlayRef("actions"));
                    return true;
                case UiAction.Left or UiAction.Right:
                    // Scrub, with the info bar showing where you are.
                    p.SeekRelative(TimeSpan.FromSeconds((a == UiAction.Left ? -1 : 1) * (repeat ? 120 : 30))); ShowInfo(); return true;
                case UiAction.Prev or UiAction.Next:
                    p.SeekRelative(TimeSpan.FromSeconds(a == UiAction.Prev ? -10 : 30)); ShowInfo(); return true;
                case UiAction.Up or UiAction.Down:
                    if (p.Current?.Kind != "live" || _ctx.Channels.Count == 0) return false;
                    _ctx.Nav.OpenOverlay(new OverlayRef("channels", a));
                    return true;
                case UiAction.PageUp or UiAction.PageDown:
                    if (p.Current?.Kind != "live") return false;
                    if (_ctx.NeighbourChannel(a == UiAction.PageUp ? 1 : -1) is { } ch) _ctx.PlayLive(ch);
                    return true;
            }
            return false;
        }

        private void ShowInfo()
        {
            if (_ctx.Playback.Current != null && _ctx.Nav.State.Overlay?.Name != "info") _ctx.Nav.OpenOverlay(new OverlayRef("info"));
        }

        private bool _running;

        private async System.Threading.Tasks.Task RunFailureActionAsync()
        {
            if (_running) return;
            _running = true;
            try
            {
                var why = await _ctx.RunFailureAction();
                if (why == null) _ctx.Playback.Replay();
                else _ctx.Toast(why);
            }
            catch (Exception ex) { _ctx.Toast(ex.Message); }
            finally { _running = false; }
        }
    }
}
