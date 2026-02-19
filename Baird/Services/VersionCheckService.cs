using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Baird.Services
{
    public class VersionCheckService
    {
        private const string TapName = "uatec/tools";
        private const string FormulaName = "baird";

        private static readonly string[] BrewPaths =
        [
            "/home/linuxbrew/.linuxbrew/bin/brew",  // Linux (linuxbrew)
            "/usr/local/bin/brew",                   // macOS Intel
            "/opt/homebrew/bin/brew",                // macOS Apple Silicon
        ];

        private static string? FindBrew()
        {
            foreach (var path in BrewPaths)
            {
                if (File.Exists(path))
                    return path;
            }
            return null;
        }

        public async Task<string?> GetLatestVersionAsync()
        {
            try
            {
                var brewPath = FindBrew();
                if (brewPath == null)
                {
                    Console.WriteLine("[VersionCheckService] Could not find brew executable");
                    return null;
                }

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = brewPath,
                    Arguments = $"info {TapName}/{FormulaName} --json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processStartInfo);
                if (process == null)
                {
                    Console.WriteLine("[VersionCheckService] Failed to start brew process");
                    return null;
                }

                // Read both stdout and stderr concurrently to avoid pipe deadlock
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                var output = await outputTask.ConfigureAwait(false);
                await errorTask.ConfigureAwait(false);
                await process.WaitForExitAsync().ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"[VersionCheckService] brew info failed with exit code {process.ExitCode}");
                    return null;
                }

                // Parse JSON to get version
                // The output is an array, looking for "version" field
                // Simple regex approach for version extraction
                var versionMatch = Regex.Match(output, @"""version"":\s*""([^""]+)""");
                if (versionMatch.Success)
                {
                    var version = versionMatch.Groups[1].Value;
                    Console.WriteLine($"[VersionCheckService] Latest version from Homebrew: {version}");
                    return version;
                }

                Console.WriteLine("[VersionCheckService] Could not parse version from brew info output");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VersionCheckService] Error checking version: {ex.Message}");
                return null;
            }
        }
    }
}
