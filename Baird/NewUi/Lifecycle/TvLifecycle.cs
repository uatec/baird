using System;
using Baird.NewUi.Playback;
using Baird.Services;

namespace Baird.NewUi.Lifecycle
{
    /// <summary>What a remote key means before the UI sees it.</summary>
    public enum RemoteInput { Other, VolumeUp, VolumeDown, Mute, Power }

    public interface IPausable
    {
        bool IsPlaying { get; }
        /// <summary>A stream is loaded, playing or paused (as opposed to only menus).</summary>
        bool HasStream => IsPlaying;
        void Pause();
        void Resume();
    }

    /// <summary>The "Still watching?" question over a paused stream.</summary>
    public interface IStillWatching { void Show(); void Hide(); }

    /// <summary>Whether Baird is idle (screensaver up, or the TV in standby), for whatever outside Baird needs to know, such as the updater.</summary>
    public interface IIdleState { void Set(bool idle); }

    public interface IScreensaver { bool IsActive { get; } void Activate(); void Deactivate(); }

    /// <summary>
    /// Baird's life with the TV, headless (ported from the classic MainView): the remote's power and volume keys drive
    /// the TV over CEC; TV standby pauses and getting the input back resumes; any key wakes the TV and claims its input
    /// (at most every 30 s); while another device has the TV, keys only ask for it back. Idle: in a menu, ten minutes
    /// start the screensaver; with a stream loaded, 2 h 55 min pause it and ask "Still watching?", and five minutes with
    /// no answer start the screensaver. Any key answers the question; the next key after the screensaver only wakes it.
    /// </summary>
    public sealed class TvLifecycle
    {
        /// <summary>Nobody watches a menu for fun.</summary>
        public static readonly TimeSpan MenuIdle = TimeSpan.FromMinutes(10);
        public static readonly TimeSpan PromptWait = TimeSpan.FromMinutes(5);   // declared before StreamIdle, which uses it
        /// <summary>Idle time with a stream loaded before asking whether anyone is still there (3 hours less the 5 minutes to answer).</summary>
        public static readonly TimeSpan StreamIdle = TimeSpan.FromHours(3) - PromptWait;
        /// <summary>How often idleness is re-checked, so a stream starting or ending is noticed.</summary>
        public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);
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

        private readonly IStillWatching? _prompt;
        private readonly IIdleState? _idleState;
        private IDisposable? _idle, _fallback, _promptTimer;
        private DateTime _lastAssert = DateTime.MinValue, _lastPower = DateTime.MinValue, _lastInteraction;
        private bool _powerInFlight, _intendedSource, _pausedForStandby, _pausedForSaver, _promptShown, _pausedForPrompt, _standby;

        public bool InputsBlocked { get; private set; }

        public TvLifecycle(ICecService cec, IPausable player, IScreensaver saver, ITimers timers, Func<DateTime> now, Action<Action> post, bool inputLock,
            IStillWatching? prompt = null, IIdleState? idleState = null)
        {
            _cec = cec; _player = player; _saver = saver; _timers = timers; _now = now; _post = post; _inputLock = inputLock;
            _prompt = prompt; _idleState = idleState;
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
            _lastInteraction = _now();
            ScheduleCheck();
            PublishIdle();
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

            var answeredPrompt = _promptShown;
            Interacted();
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
            return answeredPrompt;   // a key that answers "Still watching?" does nothing else
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
            _standby = true;
            PublishIdle();
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
            _standby = false;
            PublishIdle();
        }

        /// <summary>A key: the idle clocks start again, and an open "Still watching?" is answered.</summary>
        private void Interacted()
        {
            _lastInteraction = _now();
            _standby = false;
            if (_promptShown) HidePrompt(resume: true);
            ScheduleCheck();
            PublishIdle();
        }

        private void ScheduleCheck()
        {
            _idle?.Dispose();
            _idle = _timers.After(CheckEvery, Check);
        }

        private void Check()
        {
            if (_saver.IsActive || _promptShown) return;   // a key starts the checks again
            var idleFor = _now() - _lastInteraction;
            if (_player.HasStream)
            {
                if (idleFor >= StreamIdle) { AskStillWatching(); return; }
            }
            else if (idleFor >= MenuIdle) { StartSaver(); return; }
            ScheduleCheck();
        }

        private void AskStillWatching()
        {
            if (_prompt == null) { StartSaver(); return; }
            Console.WriteLine("[TvLifecycle] Idle with a stream loaded: asking whether anyone is still watching.");
            if (_player.IsPlaying) { _player.Pause(); _pausedForPrompt = true; }
            _promptShown = true;
            _prompt.Show();
            _promptTimer?.Dispose();
            _promptTimer = _timers.After(PromptWait, () =>
            {
                if (!_promptShown) return;
                Console.WriteLine("[TvLifecycle] No answer: screensaver.");
                HidePrompt(resume: false);
                StartSaver();
            });
        }

        private void HidePrompt(bool resume)
        {
            _promptTimer?.Dispose();
            _promptShown = false;
            _prompt?.Hide();
            if (resume && _pausedForPrompt) _player.Resume();
            if (!resume && _pausedForPrompt) _pausedForSaver = true;   // still paused: the next key after the screensaver resumes it
            _pausedForPrompt = false;
        }

        private void StartSaver()
        {
            if (_saver.IsActive) return;
            Console.WriteLine("[TvLifecycle] Idle: screensaver.");
            if (_player.IsPlaying) { _player.Pause(); _pausedForSaver = true; }
            _saver.Activate();
            PublishIdle();
        }

        private void PublishIdle() => _idleState?.Set(_saver.IsActive || _standby);

        private void WakeSaver()
        {
            _saver.Deactivate();
            if (_pausedForSaver) { _player.Resume(); _pausedForSaver = false; }
            PublishIdle();
        }
    }
}
