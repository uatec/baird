using System;

namespace Baird.NewUi.Core
{
    /// <summary>
    /// Toggles (pause/play) happen once per press: a held key's autorepeat is ignored, and so is the same press arriving
    /// again from the remote's second input device (it reports media keys on more than one), which made pause flicker.
    /// </summary>
    public sealed class ToggleGate
    {
        public static readonly TimeSpan Duplicate = TimeSpan.FromMilliseconds(400);
        private DateTime _last = DateTime.MinValue;

        public bool Pass(UiAction action, bool repeat, DateTime now)
        {
            if (action != UiAction.PlayPause) return true;
            if (repeat || now - _last < Duplicate) return false;
            _last = now;
            return true;
        }
    }
}
