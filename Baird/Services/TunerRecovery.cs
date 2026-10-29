using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>
    /// Un-wedges the satellite tuner. The USB stick can lock up (em28xx I2C error): TvHeadend then reports "No input
    /// detected" on every mux and live TV never loads. Restarting TvHeadend reloads the tuner firmware, which cleared it
    /// on the Pi (2026-10-29). Rate-limited, so a dish that is simply unplugged doesn't make it restart in a loop.
    /// </summary>
    public sealed class TunerRecovery
    {
        public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
        /// <summary>TvHeadend needs a moment to reopen the adapter before a channel can be tuned.</summary>
        public static readonly TimeSpan Settle = TimeSpan.FromSeconds(8);

        private readonly Func<Task<bool>> _restart;
        private readonly Func<DateTimeOffset> _now;
        private DateTimeOffset? _lastAttempt;
        private bool _running;

        public TunerRecovery(Func<Task<bool>> restart, Func<DateTimeOffset> now) { _restart = restart; _now = now; }

        /// <summary>Restarts TvHeadend via the sudoers rule configure_pi.sh installs (no password prompt: -n).</summary>
        public static TunerRecovery ForThisMachine() => new(RestartTvHeadendAsync, () => DateTimeOffset.Now);

        /// <summary>True if TvHeadend was restarted now; false if it was tried recently, is under way, or the restart failed.</summary>
        public async Task<bool> TryRecoverAsync()
        {
            if (_running || (_lastAttempt is { } last && _now() - last < Cooldown)) return false;
            _running = true;
            _lastAttempt = _now();
            try
            {
                Console.WriteLine("[TunerRecovery] Live TV won't load; restarting TvHeadend.");
                var ok = await _restart();
                Console.WriteLine(ok ? "[TunerRecovery] TvHeadend restarted." : "[TunerRecovery] Restarting TvHeadend failed (is the sudoers rule installed? run configure_pi.sh).");
                return ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TunerRecovery] {ex.Message}");
                return false;
            }
            finally { _running = false; }
        }

        private static async Task<bool> RestartTvHeadendAsync()
        {
            if (!OperatingSystem.IsLinux()) return false;
            using var p = Process.Start(new ProcessStartInfo("sudo", "-n systemctl restart tvheadend") { UseShellExecute = false });
            if (p == null) return false;
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
    }
}
