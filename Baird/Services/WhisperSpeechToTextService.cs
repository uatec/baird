using Microsoft.Extensions.Configuration;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.Ggml;

namespace Baird.Services
{
    public class WhisperSpeechToTextService : ISpeechToTextService, IDisposable
    {
        private static readonly string TempWavPath = Path.Combine(Path.GetTempPath(), "baird_voice.wav");

        private readonly string? _modelPath;
        private WhisperFactory? _whisperFactory;
        private Process? _recorderProcess;
        private bool _isDisposed;

        public WhisperSpeechToTextService(IConfiguration config)
        {
            _modelPath = config["WHISPER_MODEL_PATH"];
        }

        public async Task StartRecordingAsync()
        {
            // Kill any previous recording that wasn't cleaned up
            if (_recorderProcess != null && !_recorderProcess.HasExited)
            {
                try { _recorderProcess.Kill(); } catch { }
            }

            // Delete any leftover temp file
            if (File.Exists(TempWavPath))
                File.Delete(TempWavPath);

            _recorderProcess = CreateRecorderProcess();

            if (_recorderProcess == null)
            {
                Console.WriteLine("[WhisperSTT] No suitable recorder found on this platform.");
                return;
            }

            try
            {
                _recorderProcess.Start();
                Console.WriteLine($"[WhisperSTT] Recording started → {TempWavPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WhisperSTT] Failed to start recorder: {ex.Message}");
                _recorderProcess = null;
            }

            await Task.CompletedTask;
        }

        public async Task<string?> StopAndTranscribeAsync()
        {
            // ── 1. Stop the recorder ─────────────────────────────────────────
            if (_recorderProcess != null && !_recorderProcess.HasExited)
            {
                try
                {
                    // SIGINT lets arecord/sox flush and write the WAV header properly
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        _recorderProcess.Kill();
                    else
                        kill(_recorderProcess.Id, SIGINT);

                    // Give it up to 3 s to flush
                    await Task.Run(() => _recorderProcess.WaitForExit(3000));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WhisperSTT] Error stopping recorder: {ex.Message}");
                }
            }
            _recorderProcess = null;

            // ── 2. Validate the WAV file ─────────────────────────────────────
            if (!File.Exists(TempWavPath))
            {
                Console.WriteLine("[WhisperSTT] No WAV file found — nothing to transcribe.");
                return null;
            }

            var info = new FileInfo(TempWavPath);
            if (info.Length < 1024) // < 1 KB almost certainly means an empty/corrupt file
            {
                Console.WriteLine("[WhisperSTT] WAV file too small — skipping transcription.");
                return null;
            }

            // ── 3. Load model (lazy) ─────────────────────────────────────────
            if (_whisperFactory == null)
            {
                if (string.IsNullOrWhiteSpace(_modelPath) || !File.Exists(_modelPath))
                {
                    Console.WriteLine($"[WhisperSTT] Model file not found at '{_modelPath}'. " +
                                      "Set WHISPER_MODEL_PATH in config.ini.");
                    return null;
                }

                try
                {
                    _whisperFactory = WhisperFactory.FromPath(_modelPath);
                    Console.WriteLine($"[WhisperSTT] Model loaded from {_modelPath}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WhisperSTT] Failed to load model: {ex.Message}");
                    return null;
                }
            }

            // ── 4. Transcribe ────────────────────────────────────────────────
            try
            {
                using var processor = _whisperFactory.CreateBuilder()
                    .WithLanguage("auto")
                    .Build();

                using var fileStream = File.OpenRead(TempWavPath);

                var transcript = new System.Text.StringBuilder();
                await foreach (var segment in processor.ProcessAsync(fileStream))
                {
                    transcript.Append(segment.Text);
                }

                var result = transcript.ToString().Trim();
                Console.WriteLine($"[WhisperSTT] Transcript: \"{result}\"");
                return string.IsNullOrWhiteSpace(result) ? null : result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WhisperSTT] Transcription error: {ex.Message}");
                return null;
            }
            finally
            {
                try { File.Delete(TempWavPath); } catch { }
            }
        }

        // ── Platform-specific recorder factory ───────────────────────────────

        private static Process? CreateRecorderProcess()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // arecord (alsa-utils) — standard on Raspberry Pi OS
                return new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "arecord",
                        Arguments = $"-f S16_LE -r 16000 -c 1 -t wav \"{TempWavPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = false,
                    }
                };
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Prefer sox (brew install sox); fall back to ffmpeg
                if (CommandExists("sox"))
                {
                    return new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "sox",
                            // -d = default audio input device; trim silence to auto-stop optional
                            Arguments = $"-d -r 16000 -c 1 -b 16 -e signed-integer \"{TempWavPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        }
                    };
                }

                if (CommandExists("ffmpeg"))
                {
                    return new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "ffmpeg",
                            Arguments = $"-f avfoundation -i \":0\" -ar 16000 -ac 1 -sample_fmt s16 \"{TempWavPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        }
                    };
                }

                Console.WriteLine("[WhisperSTT] Neither sox nor ffmpeg found. Install via: brew install sox");
                return null;
            }

            Console.WriteLine("[WhisperSTT] Unsupported OS for audio recording.");
            return null;
        }

        private static bool CommandExists(string command)
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo("which", command)
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    }
                };
                p.Start();
                p.WaitForExit(1000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        // ── POSIX signal helpers ─────────────────────────────────────────────

        private const int SIGINT = 2;

        [DllImport("libc", EntryPoint = "kill")]
        private static extern int kill(int pid, int sig);

        // ── IDisposable ──────────────────────────────────────────────────────

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    try { _recorderProcess?.Kill(); } catch { }
                    _recorderProcess?.Dispose();
                    _whisperFactory?.Dispose();
                }
                _isDisposed = true;
            }
        }
    }
}
