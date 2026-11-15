using System;
using System.Diagnostics;
using System.Linq;

namespace Baird.Services
{
    public interface IProcessRunner { void Start(string file, string args); }

    public sealed class SystemProcessRunner : IProcessRunner
    {
        public void Start(string file, string args) =>
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false });
    }

    /// <summary>
    /// Restarts Baird (e.g. after switching between the classic and new UI). Under systemd (the Pi) it asks systemd to
    /// restart the service, so there is never a second copy outside the unit; run by hand (the PC) it starts a fresh
    /// copy with the same arguments and exits.
    /// </summary>
    public sealed class AppRestarter
    {
        private readonly IProcessRunner _runner;
        private readonly Func<string, string?> _env;
        private readonly Action _exit;
        private readonly string _processPath;
        private readonly string[] _args;

        public AppRestarter(IProcessRunner runner, Func<string, string?> env, Action exit, string processPath, string[] args)
        {
            _runner = runner; _env = env; _exit = exit; _processPath = processPath; _args = args;
        }

        public static AppRestarter ForThisProcess() => new(
            new SystemProcessRunner(), Environment.GetEnvironmentVariable, () => Environment.Exit(0),
            Environment.ProcessPath ?? "Baird", Environment.GetCommandLineArgs().Skip(1).ToArray());

        public void Restart()
        {
            Console.WriteLine("[AppRestarter] Restarting Baird.");
            if (!string.IsNullOrEmpty(_env("INVOCATION_ID")))
            {
                _runner.Start("systemctl", "--user restart baird.service");
                return;
            }
            _runner.Start(_processPath, string.Join(" ", _args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
            _exit();
        }
    }
}
