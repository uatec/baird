using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Baird.Services
{
    /// <summary>The device Baird runs on: how many OS package updates are waiting, and rebooting it.</summary>
    public sealed class SystemService
    {
        private readonly Func<Task<int?>> _count;
        private readonly Action _reboot;

        public SystemService(Func<Task<int?>> count, Action reboot) { _count = count; _reboot = reboot; }

        public static SystemService ForThisMachine() => new(CountAptUpdatesAsync, () =>
            Process.Start(new ProcessStartInfo("systemctl", "reboot") { UseShellExecute = false }));

        /// <summary>Packages waiting to be upgraded, or null when it can't be told (no apt, or it failed).</summary>
        public async Task<int?> CountUpdatesAsync()
        {
            try { return await _count(); }
            catch { return null; }
        }

        public void Reboot() => _reboot();

        // `apt-get -s upgrade` simulates (needs no root) and prints one "Inst ..." line per package it would upgrade.
        private static async Task<int?> CountAptUpdatesAsync()
        {
            var psi = new ProcessStartInfo("apt-get", "-s upgrade") { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? output.Split('\n').Count(l => l.StartsWith("Inst ")) : null;
        }
    }
}
