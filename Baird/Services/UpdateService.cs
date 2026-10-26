using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Baird.Services
{
    /// <summary>
    /// Update on demand: this version, the latest release on GitHub, and running the same updater the timer runs (with
    /// FORCE=1), detached so it survives Baird being restarted by it.
    /// </summary>
    public sealed class UpdateService
    {
        public const string ReleasesUrl = "https://api.github.com/repos/uatec/baird/releases/latest";
        public const string DefaultScript = "/home/linuxbrew/.linuxbrew/opt/baird/libexec/update_baird.sh";

        private readonly Func<Task<string?>> _latest;
        private readonly IProcessRunner _runner;
        private readonly Func<string, string?> _env;
        private readonly string _script;

        public string Current { get; }

        public UpdateService(Func<Task<string?>> latest, IProcessRunner runner, Func<string, string?> env, string script, string current)
        {
            _latest = latest; _runner = runner; _env = env; _script = script; Current = current;
        }

        public static UpdateService ForThisMachine(IConfiguration config)
        {
            var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            var current = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v0.0.0";
            return new UpdateService(FetchLatestAsync, new SystemProcessRunner(), Environment.GetEnvironmentVariable,
                config["Baird:UpdateScript"] is { Length: > 0 } s ? s : DefaultScript, current);
        }

        private static async Task<string?> FetchLatestAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Baird");
                using var doc = JsonDocument.Parse(await http.GetStringAsync(ReleasesUrl));
                return doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
            }
            catch (Exception ex) { Console.WriteLine($"[Update] Couldn't check for a new version: {ex.Message}"); return null; }
        }

        public async Task<string?> LatestAsync()
        {
            try { return await _latest(); }
            catch { return null; }
        }

        private static Version? Parse(string? tag) =>
            tag != null && Version.TryParse(tag.TrimStart('v', 'V').Split('-', '+')[0], out var v) ? v : null;

        public static bool IsNewer(string? latest, string current) =>
            Parse(latest) is { } l && Parse(current) is { } c && l > c;

        /// <summary>Starts the updater; it downloads, installs and restarts Baird (rolling back if the new one fails).</summary>
        public void StartUpdate()
        {
            Console.WriteLine($"[Update] Updating on demand with {_script}.");
            if (_env("INVOCATION_ID") is { Length: > 0 })
                // Under systemd: a transient unit of its own, so restarting baird.service doesn't kill it.
                _runner.Start("systemd-run", $"--user --collect --unit=baird-update-now --setenv=FORCE=1 {_script}");
            else
                _runner.Start("/bin/sh", $"-c \"FORCE=1 nohup '{_script}' > /tmp/baird-update.log 2>&1 &\"");
        }

        public bool ScriptExists => File.Exists(_script);
    }
}
