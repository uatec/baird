using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Baird.Services
{
    public class CecService : ICecService, IDisposable
    {
        private const string CecClientCommand = "cec-client";
        // -t p: type playback
        // -o Baird: OSD name
        // -d 1: debug level (1=error/warning only to avoid spam? or higher to parse?)
        // Let's use -d 8 for detailed traffic logging since we want to show it in debug view
        private const string CecClientArgs = "-t p -o Baird -d 8";

        private Process? _cecProcess;
        private StreamWriter? _cecInput;
        private CancellationTokenSource? _outputReadCts;
        private bool _isDisposed;

        public event EventHandler<CecCommandLoggedEventArgs>? CommandLogged;

        public async Task StartAsync()
        {
            if (_cecProcess != null && !_cecProcess.HasExited)
            {
                return;
            }

            try
            {
                _cecProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = CecClientCommand,
                        Arguments = CecClientArgs,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = false,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                _cecProcess.Start();
                _cecInput = _cecProcess.StandardInput;
                _outputReadCts = new CancellationTokenSource();

                // Start reading output in background
                _ = ReadOutputAsync(_cecProcess.StandardOutput, _outputReadCts.Token);

                Console.WriteLine("[CecService] Started cec-client interactive process");

                // Log startup
                LogCommand("Started cec-client", "Process running", true);
            }
            catch (Exception ex)
            {
                // Silently fail if cec-client is missing or crashes
                // Console.WriteLine($"[CecService] Failed to start cec-client: {ex.Message}");
                // LogCommand("Start cec-client", $"Error: {ex.Message}", false);
                _cecProcess = null;
            }
        }

        private async Task ReadOutputAsync(StreamReader reader, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break; // End of stream
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Parse the line using our new parser
                    var parsed = CecParser.ParseLine(line);
                    
                    // If parsing added valid info (indicated by brackets), use that as the description
                    // Otherwise just treat as generic event
                    string commandType = "Event";
                    string responseText = line;

                    // If parsed is different than line, it means we added interpretation
                    if (parsed != line)
                    {
                        // Extract interpretation for the main label
                        // Format is "Raw [Interpretation]"
                        var bracketIndex = parsed.LastIndexOf('[');
                        if (bracketIndex >= 0)
                        {
                            commandType = parsed.Substring(bracketIndex + 1).TrimEnd(']');
                            // Keep raw line as details
                            responseText = line; 
                        }
                    }

                    // Simple logging of all output for debug visibility
                    CommandLogged?.Invoke(this, new CecCommandLoggedEventArgs
                    {
                        Command = commandType,
                        Response = responseText,
                        Success = true,
                        Timestamp = DateTime.Now
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CecService] Error reading output: {ex.Message}");
            }
        }

        private async Task SendCommandAsync(string command, string description)
        {
            if (_cecProcess == null || _cecProcess.HasExited)
            {
                // If not running, try to start silently.
                await StartAsync();

                if (_cecProcess == null || _cecProcess.HasExited)
                {
                    // Failed to start, just return without spamming
                    // LogCommand(description, "Service not available", false);
                    return;
                }
            }

            if (_cecInput == null) return;

            try
            {
                Console.WriteLine($"[CecService] Sending: {command}");
                
                // Write with a timeout to ensure we never hang if the process buffer is full/hung
                var writeTask = _cecInput!.WriteLineAsync(command);
                var flushTask = _cecInput.FlushAsync();
                
                // 1 second timeout for simple IPC is generous
                if (await Task.WhenAny(Task.WhenAll(writeTask, flushTask), Task.Delay(1000)) != writeTask)
                {
                     throw new TimeoutException("Timed out writing to cec-client process");
                }

                LogCommand(description, $"Sent: {command}", true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CecService] Error sending command '{command}': {ex.Message}");
                LogCommand(description, $"Error: {ex.Message}", false);
            }
        }

        public async Task TogglePowerAsync()
        {
            // cec-client doesn't have a toggle, so we need to check status first
            // But checking status is async and parsing output is hard in this simple implementation
            // For now, let's just try to wake it up or prompt user
            // Alternatively, just implementation PowerOn/Off explicitly
            Console.WriteLine("[CecService] Toggle not fully supported in simple interactive mode without state tracking. Sending Power On.");
            await PowerOnAsync();
        }

        public async Task PowerOnAsync()
        {
            await SendCommandAsync("on 0", "Power On");
        }

        public async Task PowerOffAsync()
        {
            await SendCommandAsync("standby 0", "Power Off");
        }

        public async Task VolumeUpAsync()
        {
            await SendCommandAsync("volup", "Volume Up");
        }

        public async Task VolumeDownAsync()
        {
            await SendCommandAsync("voldown", "Volume Down");
        }

        public async Task ChangeInputToThisDeviceAsync()
        {
            await SendCommandAsync("as", "Active Source");
        }

        public async Task CycleInputsAsync()
        {
            // cec-client doesn't have a simple "cycle inputs" command
            // We would need to send raw tx commands?
            // "tx 10:44:00" ? (User Control Pressed: Input Select)
            // But let's stick to supported commands
            LogCommand("Cycle Inputs", "Not directly supported by cec-client simple commands", false);

            // Try sending User Control Pressed: Input Select (0x34)
            // 10:44:34
            // 1->0 (TV), 44 (UCP), 34 (Input Select)
            await SendCommandAsync("tx 10:44:34", "Cycle Inputs (Input Select)");
            await Task.Delay(100);
            await SendCommandAsync("tx 10:45", "Release Key");
        }

        public async Task<string> GetPowerStatusAsync()
        {
            // This is harder because we need to wait for the specific response in the stream
            // For now, we fire the request and let the user see the result in the log
            await SendCommandAsync("pow 0", "Get Power Status");
            return "Check Log";
        }

        private void LogCommand(string command, string response, bool success)
        {
            CommandLogged?.Invoke(this, new CecCommandLoggedEventArgs
            {
                Command = command,
                Response = response,
                Success = success,
                Timestamp = DateTime.Now
            });
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _outputReadCts?.Cancel();
                    _cecProcess?.Kill(); // Ensure process is killed
                    _cecProcess?.Dispose();
                }
                _isDisposed = true;
            }
        }
    }
}