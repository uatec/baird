using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace Baird.NewUi.Core
{
    public enum UiAction
    {
        Up, Down, Left, Right, Ok, HoldOk, Back, Home, Guide, PlayPause, Prev, Next, PageUp, PageDown, Del, Voice,
        Digit0, Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9, Help, DebugKeys, DebugFastForward,
    }

    /// <summary>
    /// Remote (G20S Pro, a USB keyboard) and keyboard keys → actions, ported from the prototype (js/keys.js).
    /// Arrows only move; panels open with specific buttons (≡ = Guide). Volume/power stay with CEC (not mapped here).
    /// </summary>
    public sealed class InputMap
    {
        private readonly Key _voiceKey;
        public InputMap(Key voiceKey = Key.None) => _voiceKey = voiceKey;

        private static readonly Dictionary<Key, UiAction> Keys = new()
        {
            [Key.Up] = UiAction.Up,
            [Key.Down] = UiAction.Down,
            [Key.Left] = UiAction.Left,
            [Key.Right] = UiAction.Right,
            [Key.Enter] = UiAction.Ok,
            [Key.Select] = UiAction.Ok,
            [Key.Escape] = UiAction.Back,
            [Key.Back] = UiAction.Back,
            [Key.BrowserBack] = UiAction.Back,
            [Key.H] = UiAction.Home,
            [Key.BrowserHome] = UiAction.Home,
            [Key.G] = UiAction.Guide,
            [Key.Apps] = UiAction.Guide,
            [Key.Space] = UiAction.PlayPause,
            [Key.MediaPlayPause] = UiAction.PlayPause,
            [Key.Play] = UiAction.PlayPause,
            [Key.Pause] = UiAction.PlayPause,
            [Key.OemOpenBrackets] = UiAction.Prev,
            [Key.MediaPreviousTrack] = UiAction.Prev,
            [Key.OemCloseBrackets] = UiAction.Next,
            [Key.MediaNextTrack] = UiAction.Next,
            [Key.PageUp] = UiAction.PageUp,
            [Key.PageDown] = UiAction.PageDown,
            [Key.Delete] = UiAction.Del,
            [Key.V] = UiAction.Voice,
            [Key.OemQuestion] = UiAction.Help,
            [Key.K] = UiAction.DebugKeys,
            [Key.F] = UiAction.DebugFastForward,
        };

        /// <summary>MediaTvGuide arrives as this raw key code on some remotes (as in the classic UI).</summary>
        public const int MediaTvGuideCode = 100026;

        public UiAction? Map(Key key)
        {
            if (_voiceKey != Key.None && key == _voiceKey) return UiAction.Voice;
            if ((int)key == MediaTvGuideCode) return UiAction.Guide;
            if (key >= Key.D0 && key <= Key.D9) return UiAction.Digit0 + (key - Key.D0);
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return UiAction.Digit0 + (key - Key.NumPad0);
            return Keys.TryGetValue(key, out var a) ? a : null;
        }

        /// <summary>The remote is also an air mouse: OK arrives as the left button, Back and Home as the others.</summary>
        public static UiAction? MapButton(PointerUpdateKind kind) => kind switch
        {
            PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => UiAction.Ok,
            PointerUpdateKind.RightButtonPressed or PointerUpdateKind.XButton1Pressed => UiAction.Back,
            PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.XButton2Pressed => UiAction.Home,
            _ => null,
        };

        public static int? DigitOf(UiAction a) => a >= UiAction.Digit0 && a <= UiAction.Digit9 ? a - UiAction.Digit0 : null;
    }

    /// <summary>OK down/up → Ok (short press, on release) or HoldOk (fires once at the threshold).</summary>
    public sealed class HoldDetector
    {
        private readonly TimeSpan _hold;
        private readonly Func<DateTime> _now;
        private DateTime? _downAt;
        private bool _fired;

        public HoldDetector(TimeSpan? hold = null, Func<DateTime>? now = null)
        {
            _hold = hold ?? TimeSpan.FromMilliseconds(500);
            _now = now ?? (() => DateTime.UtcNow);
        }

        private DateTime _lastSeen;
        /// <summary>No word from a press for this long: its release was lost, so the next press starts afresh.</summary>
        public static readonly TimeSpan Stale = TimeSpan.FromSeconds(1);

        public void Down()
        {
            var now = _now();
            // A held key repeats its down events; silence means the release went missing (e.g. it went to another window),
            // and treating the old press as still held would turn the next quick click into a hold.
            if (_downAt != null && now - _lastSeen <= Stale) { _lastSeen = now; return; }
            _downAt = now; _lastSeen = now; _fired = false;
        }

        public UiAction? Tick()
        {
            if (_downAt == null || _fired) return null;
            if (_now() - _downAt.Value >= _hold) { _fired = true; return UiAction.HoldOk; }
            return null;
        }

        public UiAction? Up()
        {
            if (_downAt == null) return null;
            UiAction? r = _fired ? null : (Tick() ?? UiAction.Ok);
            _downAt = null;
            return r;
        }
    }

    /// <summary>Typed channel numbers: "1 0 4" → Channel 4; numbers that don't exist say so.</summary>
    public sealed class NumberEntry
    {
        public sealed record Result(string Text, string? Match, string? Error);
        private readonly IReadOnlyCollection<string> _numbers;
        private string _text = "";
        public NumberEntry(IEnumerable<string> channelNumbers) => _numbers = channelNumbers.ToList();

        public Result State()
        {
            if (_text.Length == 0) return new("", null, null);
            var match = _numbers.FirstOrDefault(n => n == _text);
            var anyPrefix = _numbers.Any(n => n.StartsWith(_text, StringComparison.Ordinal));
            return new(_text, match, match == null && !anyPrefix ? $"No channel {_text}" : null);
        }
        public Result Push(int digit) { if (_text.Length < 4) _text += digit; return State(); }
        public Result Del() { if (_text.Length > 0) _text = _text[..^1]; return State(); }
        public Result Clear() { _text = ""; return State(); }
        /// <summary>True when the typed number is a channel and no longer number starts with it (tune straight away).</summary>
        public bool IsUnique => State().Match != null && !_numbers.Any(n => n != _text && n.StartsWith(_text, StringComparison.Ordinal));
    }
}
