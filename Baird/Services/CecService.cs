using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
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
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        // Our logical address, learned from the frames we transmit. cec-client picks it at registration:
        // Playback 1 (4) is usually taken by another box (e.g. a Chromecast), so we often end up as 8.
        // Raw "tx" commands must use it as their source nibble. 0xF (unregistered) until known.
        private volatile int _logicalAddress = 0xF;

        private TaskCompletionSource<TvPowerStatus>? _pendingPowerStatus;
        private static readonly TimeSpan PowerStatusTimeout = TimeSpan.FromSeconds(2);

        // cec-client answers "pow 0" on stdout with e.g. "power status: on" or
        // "power status: in transition from standby to on".
        private static readonly Regex PowerStatusLineRegex = new(@"^power status:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // CEC User Control codes (CEC 1.4 table 27).
        private const byte UiVolumeUp = 0x41;
        private const byte UiVolumeDown = 0x42;
        private const byte UiMute = 0x43;
        private const byte UiInputSelect = 0x34;

        public event EventHandler<CecCommandLoggedEventArgs>? CommandLogged;
        public event EventHandler? TvStandby;
        public event EventHandler? TvPowerOn;
        public event EventHandler? InputLost;
        public event EventHandler? InputRegained;
        public event EventHandler<CecUserControlEventArgs>? UserControlReceived;

        public bool IsAvailable => _cecProcess != null && !_cecProcess.HasExited;

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
                Console.WriteLine($"[CecService] Failed to start cec-client: {ex.Message}");
                LogCommand("Start cec-client", $"Error: {ex.Message}", false);
                _cecProcess = null;
            }
        }

        private async Task ReadOutputAsync(StreamReader reader, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(token);
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

                    TrackFrame(line);

                    // Simple logging of all output for debug visibility
                    CommandLogged?.Invoke(this, new CecCommandLoggedEventArgs
                    {
                        Command = commandType,
                        Response = responseText,
                        Success = true,
                        Timestamp = DateTime.Now
                    });

                    // Incoming traffic (>>) — events from other devices on the CEC bus.
                    // We deliberately ignore outgoing (<<) lines for these to avoid feedback loops
                    // where our own auto-responses re-trigger events.
                    if (line.Contains(">>"))
                    {
                        if (commandType.Contains("Standby"))
                        {
                            Console.WriteLine("[CecService] TV standby detected.");
                            TvStandby?.Invoke(this, EventArgs.Empty);
                        }
                        else if (commandType.Contains("Image View On")
                              || commandType.Contains("Text View On"))
                        {
                            Console.WriteLine("[CecService] TV power on detected.");
                            TvPowerOn?.Invoke(this, EventArgs.Empty);
                        }
                        else if (commandType.Contains("Active Source"))
                        {
                            Console.WriteLine("[CecService] Active Source from another device — input lost.");
                            InputLost?.Invoke(this, EventArgs.Empty);
                        }
                    }

                    // Incoming Request Active Source (>>) means the TV has finished booting/switching
                    // and is actively asking which device should be displayed. This is the reliable
                    // signal that the TV is ready — more accurate than our own outgoing Active Source
                    // echo, which only confirms the command was sent, not that the TV is ready.
                    if (line.Contains(">>") && commandType.Contains("Request Active Source"))
                    {
                        Console.WriteLine("[CecService] TV requested active source — TV is ready, input regained.");
                        InputRegained?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CecService] Error reading output: {ex.Message}");
            }
        }

        private void TrackFrame(string line)
        {
            var statusMatch = PowerStatusLineRegex.Match(line.Trim());
            if (statusMatch.Success)
            {
                _pendingPowerStatus?.TrySetResult(ParsePowerStatusText(statusMatch.Groups[1].Value));
                return;
            }

            // Polls (single byte) are skipped: during address allocation cec-client polls candidate
            // addresses it may not end up with.
            if (!CecParser.TryParseFrame(line, out var incoming, out var frame) || frame.Length < 2) return;

            if (!incoming)
            {
                _logicalAddress = frame[0] >> 4;
            }
            else if (CecParser.TryParseUserControl(frame, _logicalAddress, out var userControl))
            {
                UserControlReceived?.Invoke(this, userControl);
            }
            else if (frame[1] is 0x89 or 0x8A or 0x8B or 0x8D or 0xA0)
            {
                // Some TVs pass remote keys as vendor commands rather than User Control, and some only pass keys
                // once the device answers Menu Request. Logged so a TV whose keys don't arrive can be diagnosed.
                Console.WriteLine($"[CecService] Remote-related frame: {CecParser.ParseLine(line)}");
            }
            else if (frame[0] >> 4 == 0 && frame[1] == 0x90 && frame.Length > 2)
            {
                // Report Power Status from the TV.
                _pendingPowerStatus?.TrySetResult(frame[2] switch
                {
                    0x00 => TvPowerStatus.On,
                    0x01 => TvPowerStatus.Standby,
                    0x02 => TvPowerStatus.TransitioningToOn,
                    0x03 => TvPowerStatus.TransitioningToStandby,
                    _ => TvPowerStatus.Unknown,
                });
            }
        }

        private static TvPowerStatus ParsePowerStatusText(string text)
        {
            text = text.Trim().ToLowerInvariant();
            if (text.Contains("standby to on")) return TvPowerStatus.TransitioningToOn;
            if (text.Contains("on to standby")) return TvPowerStatus.TransitioningToStandby;
            if (text == "on") return TvPowerStatus.On;
            if (text == "standby") return TvPowerStatus.Standby;
            return TvPowerStatus.Unknown;
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

            // Commands are fired concurrently (e.g. power on + active source, held volume keys), and
            // StreamWriter does not allow overlapping async writes.
            await _writeLock.WaitAsync();
            try
            {
                Console.WriteLine($"[CecService] Sending: {command}");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _cecInput!.WriteLineAsync(command.AsMemory(), cts.Token);
                await _cecInput.FlushAsync(cts.Token);

                LogCommand(description, $"Sent: {command}", true);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[CecService] Timed out sending command '{command}'");
                LogCommand(description, "Error: Timed out writing to cec-client process", false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CecService] Error sending command '{command}': {ex.Message}");
                LogCommand(description, $"Error: {ex.Message}", false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>Sends a remote-control key press + release to the TV, as if pressed on its own remote.</summary>
        private async Task SendUserControlToTvAsync(byte uiCommand, string description)
        {
            var header = $"{_logicalAddress:x}0";
            await SendCommandAsync($"tx {header}:44:{uiCommand:x2}", description);
            await SendCommandAsync($"tx {header}:45", "Release Key");
        }

        public async Task<bool> TogglePowerAsync()
        {
            var status = await GetPowerStatusAsync();

            // An unanswered query turns the TV on: harmless if it was already on, whereas a blind
            // standby could switch off a TV that is in use.
            var turnOn = status is TvPowerStatus.Standby or TvPowerStatus.TransitioningToStandby or TvPowerStatus.Unknown;
            Console.WriteLine($"[CecService] Toggle power: TV is {status}, turning it {(turnOn ? "on" : "off")}.");

            if (turnOn) await PowerOnAsync();
            else await PowerOffAsync();
            return turnOn;
        }

        public async Task PowerOnAsync()
        {
            await SendCommandAsync("on 0", "Power On");
        }

        public async Task PowerOffAsync()
        {
            await SendCommandAsync("standby 0", "Power Off");
        }

        // cec-client's volup/voldown/mute only address a CEC audio system (amp/soundbar), so with none
        // on the bus they do nothing. Sending the keys to the TV works either way: it adjusts its own
        // speakers or forwards to an ARC amp.
        public Task VolumeUpAsync() => SendUserControlToTvAsync(UiVolumeUp, "Volume Up");

        public Task VolumeDownAsync() => SendUserControlToTvAsync(UiVolumeDown, "Volume Down");

        public Task MuteAsync() => SendUserControlToTvAsync(UiMute, "Mute");

        public async Task ChangeInputToThisDeviceAsync()
        {
            await SendCommandAsync("as", "Active Source");
        }

        public async Task CycleInputsAsync()
        {
            // cec-client has no "cycle inputs" command, so press the TV's Input Select key.
            await SendUserControlToTvAsync(UiInputSelect, "Cycle Inputs (Input Select)");
        }

        public async Task<TvPowerStatus> GetPowerStatusAsync()
        {
            var pending = new TaskCompletionSource<TvPowerStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingPowerStatus = pending;

            await SendCommandAsync("pow 0", "Get Power Status");

            var completed = await Task.WhenAny(pending.Task, Task.Delay(PowerStatusTimeout));
            return completed == pending.Task ? pending.Task.Result : TvPowerStatus.Unknown;
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