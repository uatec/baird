using System;
using Baird.NewUi.Playback;
using Baird.Services;

namespace Baird.NewUi.Lifecycle
{
    /// <summary>What a remote key means before the UI sees it.</summary>
    public enum RemoteInput { Other, VolumeUp, VolumeDown, Mute, Power }

    public interface IPausable { bool IsPlaying { get; } void Pause(); void Resume(); }

    public interface IScreensaver { bool IsActive { get; } void Activate(); void Deactivate(); }

    /// <summary>
    /// Baird's life with the TV, headless (ported from the classic MainView): the remote's power and volume keys drive
    /// the TV over CEC; TV standby pauses and getting the input back resumes; any key wakes the TV and claims its input
    /// (at most every 30 s); while another device has the TV, keys only ask for it back; two idle hours start the
    /// screensaver, and the next key only wakes it.
    /// </summary>
    public sealed class TvLifecycle
    {
        public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(2);
        public static readonly TimeSpan AssertCooldown = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan PowerCooldown = TimeSpan.FromSeconds(2);
        public static readonly TimeSpan UnblockFallback = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan VolumeRepeat = TimeSpan.FromMilliseconds(300);

        private readonly ICecService _cec;
        private readonly IPausable _player;
        private readonly IScreensaver _saver;
        private readonly ITimers _timers;
        private readonly Func<DateTime> _now;
        private readonly Action<Action> _post;
        private readonly bool _inputLock;
        private readonly RemoteKeyGate _volUp = new(VolumeRepeat), _volDown = new(VolumeRepeat), _mute = new(TimeSpan.MaxValue);

        private IDisposable? _idle, _fallback;
        private DateTime _lastAssert = DateTime.MinValue, _lastPower = DateTime.MinValue;
        private bool _powerInFlight, _intendedSource, _pausedForStandby, _pausedForSaver;

        public bool InputsBlocked { get; private set; }

        public TvLifecycle(ICecService cec, IPausable player, IScreensaver saver, ITimers timers, Func<DateTime> now, Action<Action> post, bool inputLock)
        {
            _cec = cec; _player = player; _saver = saver; _timers = timers; _now = now; _post = post; _inputLock = inputLock;
        }

        public void Start()
        {
            // CEC events arrive on a background thread.
            _cec.TvStandby += (_, _) => _post(OnStandby);
            _cec.TvPowerOn += (_, _) => _post(OnTvPowerOn);
            _cec.InputLost += (_, _) => _post(() =>
            {
                Console.WriteLine("[TvLifecycle] Another device took the TV.");
                _intendedSource = false;
                if (_inputLock) InputsBlocked = true;
            });
            _cec.InputRegained += (_, _) => _post(() => { _intendedSource = true; Unblock(); });
            ArmIdle();
            _ = StartCecAsync();
        }

        private async System.Threading.Tasks.Task StartCecAsync()
        {
            try { await _cec.StartAsync(); }
            catch (Exception ex) { Console.WriteLine($"[TvLifecycle] CEC failed to start: {ex.Message}"); return; }
            if (!_cec.IsAvailable) return;
            // Just started (maybe after an update with the TV off): claim the TV; keys wait until it confirms.
            _post(() =>
            {
                _intendedSource = true;
                if (_inputLock) { InputsBlocked = true; ArmFallback(); }
                _ = _cec.ChangeInputToThisDeviceAsync();
            });
        }

        /// <summary>Handle a key before the UI. True = consumed (the UI must not act on it).</summary>
        public bool HandleInput(RemoteInput input)
        {
            var now = _now();
            switch (input)
            {
                case RemoteInput.VolumeUp: if (_volUp.ShouldSend(now)) _ = _cec.VolumeUpAsync(); return true;
                case RemoteInput.VolumeDown: if (_volDown.ShouldSend(now)) _ = _cec.VolumeDownAsync(); return true;
                case RemoteInput.Mute: if (_mute.ShouldSend(now)) _ = _cec.MuteAsync(); return true;
                case RemoteInput.Power: _ = TogglePowerAsync(); return true;
            }

            if (InputsBlocked)
            {
                Console.WriteLine("[TvLifecycle] Key while the TV isn't ours: asking for it back.");
                RequestInput();
                return true;
            }

            ArmIdle();
            if (now - _lastAssert >= AssertCooldown)
            {
                _lastAssert = now;
                _intendedSource = true;
                _ = _cec.PowerOnAsync();
                _ = _cec.ChangeInputToThisDeviceAsync();
            }

            if (_saver.IsActive)
            {
                WakeSaver();
                return true;
            }
            return false;
        }

        private async System.Threading.Tasks.Task TogglePowerAsync()
        {
            // A held power key autorepeats, and the remote reports it on more than one device: one toggle per press.
            var now = _now();
            if (_powerInFlight || now - _lastPower < PowerCooldown) return;
            _powerInFlight = true;
            _lastPower = now;
            try
            {
                if (await _cec.TogglePowerAsync()) RequestInput();
                else OnStandby(); // the TV doesn't announce a standby we asked for
            }
            catch (Exception ex) { Console.WriteLine($"[TvLifecycle] Power toggle failed: {ex.Message}"); }
            finally { _powerInFlight = false; }
        }

        private void OnStandby()
        {
            Console.WriteLine("[TvLifecycle] TV standby.");
            if (_inputLock) InputsBlocked = true;
            if (_player.IsPlaying) { _player.Pause(); _pausedForStandby = true; }
        }

        private void OnTvPowerOn()
        {
            // Any device waking the TV sends this: only claim the input if Baird was the one in use.
            if (!_intendedSource) { if (InputsBlocked) ArmFallback(); return; }
            _ = _cec.ChangeInputToThisDeviceAsync();
        }

        private void RequestInput()
        {
            _intendedSource = true;
            _ = _cec.PowerOnAsync();
            _ = _cec.ChangeInputToThisDeviceAsync();
            ArmFallback();
        }

        private void ArmFallback()
        {
            // If the TV switches without sending Request Active Source, unblock anyway.
            _fallback?.Dispose();
            _fallback = _timers.After(UnblockFallback, () => { if (InputsBlocked) Unblock(); });
        }

        private void Unblock()
        {
            _fallback?.Dispose();
            InputsBlocked = false;
            if (_saver.IsActive) _saver.Deactivate();
            if (_pausedForStandby || _pausedForSaver) _player.Resume();
            _pausedForStandby = _pausedForSaver = false;
        }

        private void ArmIdle()
        {
            _idle?.Dispose();
            _idle = _timers.After(IdleTimeout, () =>
            {
                if (_saver.IsActive) return;
                Console.WriteLine("[TvLifecycle] Two idle hours: screensaver.");
                if (_player.IsPlaying) { _player.Pause(); _pausedForSaver = true; }
                _saver.Activate();
            });
        }

        private void WakeSaver()
        {
            _saver.Deactivate();
            if (_pausedForSaver) { _player.Resume(); _pausedForSaver = false; }
        }
    }
}
