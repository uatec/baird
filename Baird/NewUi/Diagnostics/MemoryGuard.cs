using System;

namespace Baird.NewUi.Diagnostics
{
    /// <summary>
    /// Last line of defence against slow memory growth on the 8 GB Pi: once Baird's own (anonymous) memory passes a limit,
    /// and nothing is playing and nobody has pressed a key for a while, Baird restarts itself, so a leak shows up as a
    /// quiet restart in the small hours instead of Home hanging for minutes in swap.
    /// </summary>
    public sealed class MemoryGuard
    {
        public const long DefaultLimitKb = 3500L * 1024;
        public static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(10);

        private readonly Func<long> _anonKb;
        private readonly Func<bool> _playing;
        private readonly Func<DateTime> _lastInput;
        private readonly Func<DateTime> _now;
        private readonly Action _restart;
        private readonly long _limitKb;
        private bool _restarting;

        public MemoryGuard(Func<long> anonKb, Func<bool> playing, Func<DateTime> lastInput, Func<DateTime> now, Action restart, long limitKb = DefaultLimitKb)
        {
            _anonKb = anonKb; _playing = playing; _lastInput = lastInput; _now = now; _restart = restart; _limitKb = limitKb;
        }

        /// <summary>Call about once a minute. Returns true if it asked for a restart.</summary>
        public bool Check()
        {
            if (_restarting) return false;
            var anon = _anonKb();
            if (anon < _limitKb || _playing() || _now() - _lastInput() < IdleFor) return false;
            _restarting = true;
            Console.WriteLine($"[MemoryGuard] Baird is using {anon / 1024} MB and has been idle; restarting to give it back.");
            _restart();
            return true;
        }
    }
}
