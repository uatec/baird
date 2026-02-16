using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Baird.Services
{
    public class VersionCheckService
    {
        private const string TapName = "uatec/tools";
        private const string FormulaName = "baird";
        
        public async Task<string?> GetLatestVersionAsync()
        {
            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "brew",
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

                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

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
