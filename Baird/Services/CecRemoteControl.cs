using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;

namespace Baird.Services
{
    /// <summary>
    /// Lets the TV's own remote drive Baird: when Baird is the input on screen, the TV passes most of its remote's keys
    /// on over CEC (User Control Pressed/Released). Each one becomes the key press Baird already handles from the USB
    /// remote. Volume, mute and power stay with the TV — it acts on those itself and doesn't pass them on.
    ///
    /// Every key the TV sends is logged with a "[CecRemote]" prefix (journalctl -t baird | grep CecRemote), including
    /// keys for other devices and keys Baird doesn't map, so a remote that does nothing can be diagnosed.
    /// </summary>
    public sealed class CecRemoteControl : IDisposable
    {
        /// <summary>
        /// CEC 1.4 §13.13: a device that hears neither a repeat nor a Released within this long treats the key as let go.
        /// </summary>
        public static readonly TimeSpan DefaultReleaseTimeout = TimeSpan.FromMilliseconds(550);

        // CEC 1.4 table 27 → the keys Baird handles from the USB remote and keyboard.
        private static readonly Dictionary<byte, Key> KeyMap = new()
        {
            { 0x00, Key.Enter },              // Select (OK)
            { 0x01, Key.Up },
            { 0x02, Key.Down },
            { 0x03, Key.Left },
            { 0x04, Key.Right },
            { 0x09, Key.BrowserHome },        // Root Menu
            { 0x0D, Key.BrowserBack },        // Exit (Back/Return)
            { 0x20, Key.D0 },
            { 0x21, Key.D1 },
            { 0x22, Key.D2 },
            { 0x23, Key.D3 },
            { 0x24, Key.D4 },
            { 0x25, Key.D5 },
            { 0x26, Key.D6 },
            { 0x27, Key.D7 },
            { 0x28, Key.D8 },
            { 0x29, Key.D9 },
            { 0x2B, Key.Enter },
            { 0x30, Key.PageUp },             // Channel Up
            { 0x31, Key.PageDown },           // Channel Down
            { 0x44, Key.Play },
            { 0x45, Key.MediaStop },
            { 0x46, Key.Pause },
            { 0x48, Key.Left },               // Rewind: seeks back in the player
            { 0x49, Key.Right },              // Fast Forward: seeks forward in the player
            { 0x51, Key.Apps },               // Subtitles
            { 0x53, Key.G },                  // EPG (Guide)
            { 0x61, Key.MediaPlayPause },     // Pause-Play Function
        };

        private readonly ICecService _cec;
        private readonly Action<Key, bool> _sendKey;
        private readonly TimeSpan _releaseTimeout;
        private readonly object _lock = new();
        private Key? _held;
        private CancellationTokenSource? _releaseTimer;

        /// <param name="sendKey">Raises a key press (true) or release (false) in the UI. Called from CEC's reader thread.</param>
        public CecRemoteControl(ICecService cec, Action<Key, bool> sendKey, TimeSpan? releaseTimeout = null)
        {
            _cec = cec;
            _sendKey = sendKey;
            _releaseTimeout = releaseTimeout ?? DefaultReleaseTimeout;
            _cec.UserControlReceived += OnUserControl;
        }

        public static bool TryMapKey(byte code, out Key key) => KeyMap.TryGetValue(code, out key);

        private void OnUserControl(object? sender, CecUserControlEventArgs e)
        {
            var from = CecParser.AddressName(e.Source);
            if (!e.ForThisDevice)
            {
                var what = e.Code is { } other ? CecParser.UserControlName(other) : "release";
                Console.WriteLine($"[CecRemote] {from} sent '{what}' to {CecParser.AddressName(e.Destination)}, not Baird — ignored.");
                return;
            }

            if (e.Code is not { } code)
            {
                Release("released");
                return;
            }

            var name = CecParser.UserControlName(code);
            if (!TryMapKey(code, out var key))
            {
                Console.WriteLine($"[CecRemote] {from} pressed '{name}' (0x{code:X2}) — Baird has no action for it.");
                Release("another key pressed");
                return;
            }

            lock (_lock)
            {
                if (_held is { } previous && previous != key)
                {
                    Console.WriteLine($"[CecRemote] Release {previous} (another key pressed).");
                    _sendKey(previous, false);
                }
                _held = key;
                _releaseTimer?.Cancel();
                _releaseTimer = new CancellationTokenSource();
                _ = ReleaseAfterTimeoutAsync(_releaseTimer);
            }

            Console.WriteLine($"[CecRemote] {from} pressed '{name}' (0x{code:X2}) → {key}.");
            _sendKey(key, true);
        }

        private async Task ReleaseAfterTimeoutAsync(CancellationTokenSource timer)
        {
            try
            {
                await Task.Delay(_releaseTimeout, timer.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            Release($"no release from the TV within {_releaseTimeout.TotalMilliseconds:0} ms", timer);
        }

        /// <param name="timer">When the release timer fires: only release if no later press has replaced it.</param>
        private void Release(string reason, CancellationTokenSource? timer = null)
        {
            Key key;
            lock (_lock)
            {
                if (timer != null && timer != _releaseTimer) return;
                if (_held is not { } held) return;
                key = held;
                _held = null;
                _releaseTimer?.Cancel();
                _releaseTimer = null;
            }
            Console.WriteLine($"[CecRemote] Release {key} ({reason}).");
            _sendKey(key, false);
        }

        public void Dispose()
        {
            _cec.UserControlReceived -= OnUserControl;
            lock (_lock)
            {
                _releaseTimer?.Cancel();
                _releaseTimer = null;
            }
        }
    }
}
