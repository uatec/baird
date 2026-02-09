using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Baird.Services
{
    public class CecService : ICecService
    {
        private const string CecCtlCommand = "cec-ctl";
        // Default to device 0 (TV) and adapter 0 (/dev/cec0)
        private const string DeviceArg = "-d0"; 
        private const string TargetArg = "--to 0";

        public Task StartAsync()
        {
            // Optional: Check if cec-ctl is available or perform initial ping
            return Task.CompletedTask;
        }

        public async Task TogglePowerAsync()
        {
            try
            {
                var isOn = await IsTvOnAsync();
                Console.WriteLine($"[CecService] TV Power Status: {(isOn ? "On" : "Standby/Unknown")}");

                if (isOn)
                {
                    await SendStandbyAsync();
                }
                else
                {
                    await SendPowerOnAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CecService] Error toggling power: {ex.Message}");
            }
        }

        private async Task<bool> IsTvOnAsync()
        {
            // cec-ctl -d0 --to 0 --give-device-power-status
            var output = await RunCecCtlAsync($"{DeviceArg} {TargetArg} --give-device-power-status");
            
            // Output example:
            // ...
            // CEC_MSG_REPORT_POWER_STATUS (0x90):
            //     pwr-state: on (0x00)
            
            // or: pwr-state: standby (0x01)
            
            return output.Contains("pwr-state: on", StringComparison.OrdinalIgnoreCase);
        }

        private async Task SendStandbyAsync()
        {
            Console.WriteLine("[CecService] Sending Standby...");
            await RunCecCtlAsync($"{DeviceArg} {TargetArg} --standby");
        }

        private async Task SendPowerOnAsync()
        {
            Console.WriteLine("[CecService] Sending Power On (Image View On)...");
            // --image-view-on is the standard way to wake a TV
            await RunCecCtlAsync($"{DeviceArg} {TargetArg} --image-view-on");
        }

        private async Task<string> RunCecCtlAsync(string arguments)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = CecCtlCommand,
                        Arguments = arguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                await process.WaitForExitAsync();
                
                var output = await process.StandardOutput.ReadToEndAsync();
                var error = await process.StandardError.ReadToEndAsync();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"[CecService] Command failed (Exit Code {process.ExitCode}): {CecCtlCommand} {arguments}");
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        Console.WriteLine($"[CecService] Error Output: {error}");
                    }
                }
                
                return output;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.WriteLine($"[CecService] '{CecCtlCommand}' not found. Ensure v4l-utils is installed.");
                return string.Empty;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CecService] Execution error: {ex.Message}");
                return string.Empty;
            }
        }
    }
}
