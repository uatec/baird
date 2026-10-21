using System;

namespace Baird.Services
{
    /// <summary>
    /// Decides which key events from a remote button become CEC commands. A held key autorepeats at
    /// ~25/s, but the CEC bus carries only ~4 key presses/s; anything beyond that queues up in
    /// cec-client and keeps changing the TV long after the button is let go. So repeats are throttled
    /// to <c>repeatInterval</c>, and a "hold" longer than <see cref="MaxHold"/> is cut off — the
    /// display server can lose a key release and autorepeat forever.
    /// </summary>
    public sealed class RemoteKeyGate
    {
        public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(5);

        // Longer than the autorepeat delay (~600 ms), so the first repeat isn't taken as a new press.
        private static readonly TimeSpan ReleaseGap = TimeSpan.FromSeconds(1);

        private readonly TimeSpan _repeatInterval;
        private DateTime _lastEvent = DateTime.MinValue;
        private DateTime _pressStart;
        private DateTime _lastSend;

        /// <param name="repeatInterval">Minimum time between sends while held; <see cref="TimeSpan.MaxValue"/> sends once per press.</param>
        public RemoteKeyGate(TimeSpan repeatInterval)
        {
            _repeatInterval = repeatInterval;
        }

        public bool ShouldSend(DateTime now)
        {
            var newPress = now - _lastEvent > ReleaseGap;
            _lastEvent = now;

            if (newPress)
            {
                _pressStart = now;
                _lastSend = now;
                return true;
            }

            if (now - _pressStart > MaxHold || now - _lastSend < _repeatInterval) return false;

            _lastSend = now;
            return true;
        }
    }
}
