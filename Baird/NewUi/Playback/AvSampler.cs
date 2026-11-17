using System;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.NewUi.Playback
{
    /// <summary>
    /// One periodic sample of the player's dropped-frame total, for a timer tick. A sample that throws is ignored (the
    /// tick never faults), and a tick while the previous sample is still waiting on the player is skipped, so a stalled
    /// player cannot pile up requests.
    /// </summary>
    public sealed class AvSampler
    {
        private readonly Func<Task<long?>> _sample;
        private readonly Action<long> _store;
        private int _busy;

        public AvSampler(Func<Task<long?>> sample, Action<long> store) { _sample = sample; _store = store; }

        public async Task TickAsync()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                if (await _sample().ConfigureAwait(false) is { } total) _store(total);
            }
            catch (Exception ex) { Console.WriteLine($"[WarmGate] Sampling dropped frames failed: {ex.Message}"); }
            finally { Volatile.Write(ref _busy, 0); }
        }
    }
}
