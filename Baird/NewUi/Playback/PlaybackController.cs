using System;
using Baird.Mpv;
using Baird.NewUi.Model;
using Baird.Playback;

namespace Baird.NewUi.Playback
{
    /// <summary>One-shot timers (the real one uses the dispatcher; tests advance a fake clock).</summary>
    public interface ITimers { IDisposable After(TimeSpan delay, Action action); }

    public sealed class DispatcherTimers : ITimers
    {
        public IDisposable After(TimeSpan delay, Action action) => Avalonia.Threading.DispatcherTimer.RunOnce(action, delay);
    }

    public sealed record NowPlaying(string Kind, string ItemId, string Title, string Url, Channel? Channel);

    /// <summary>
    /// Headless playback over an <see cref="IPlaybackEngine"/>: load timeout and retries, debounced channel changes,
    /// pause/seek with time behind live, and saving progress. Nothing here touches controls; the video surface only renders.
    /// Engine events can arrive on any thread; they are marshalled through <c>post</c>.
    /// </summary>
    public sealed class PlaybackController
    {
        public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);
        public const int MaxRetries = 3;
        /// <summary>Flicking channels: only the channel you rest on is tuned.</summary>
        public static readonly TimeSpan TuneDebounce = TimeSpan.FromMilliseconds(250);
        /// <summary>Watching this long puts a channel or video in history (Home's Continue watching); less is just flicking past.</summary>
        public static readonly TimeSpan WatchedAfter = TimeSpan.FromSeconds(60);

        private readonly IPlaybackEngine _engine;
        private readonly Action<Action> _post;
        private readonly ITimers _timers;
        private readonly Func<DateTimeOffset> _now;
        private readonly Action<string, TimeSpan, TimeSpan>? _save;
        private readonly Action<bool>? _configure;

        private IDisposable? _loadTimer, _tuneTimer, _watchTimer;
        private bool _watched;
        private int _retries;
        private double? _start;
        private DateTimeOffset? _pausedAt;
        private bool _saved;
        private DateTimeOffset _loadedAt;
        private int _quickDrops;
        /// <summary>A live stream that ends this soon after loading counts towards giving up.</summary>
        private static readonly TimeSpan QuickDrop = TimeSpan.FromSeconds(10);
        private const int MaxQuickDrops = 3;

        public NowPlaying? Current { get; private set; }
        public bool IsLoading { get; private set; }
        public string? Failure { get; private set; }
        public TimeSpan BehindLive { get; private set; }
        public bool IsPaused { get; private set; }
        /// <summary>What loaded is sound only (a radio channel): there is no picture to show.</summary>
        public bool IsAudioOnly { get; private set; }
        public PlaybackState State => _engine.State;
        public TimeSpan Position => TimeSpan.FromSeconds(Math.Max(0, _engine.TimePositionSeconds));
        public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0, _engine.DurationSeconds));

        public event Action? Changed;
        public event Action? Ended;
        /// <summary>The engine has been asked to change stream (after the tune debounce): only now may the picture change.</summary>
        public event Action? Started;
        /// <summary>The first frame of a newly loaded stream is on its way (tuning can end).</summary>
        public event Action? Loaded;

        /// <param name="configure">Sets mpv up for the source before each play (true = live: deinterlace, no loudnorm).</param>
        public PlaybackController(IPlaybackEngine engine, Action<Action> post, ITimers timers, Func<DateTimeOffset> now, Action<string, TimeSpan, TimeSpan>? save,
            Action<bool>? configure = null)
        {
            _engine = engine; _post = post; _timers = timers; _now = now; _save = save; _configure = configure;
            if (engine is EngineRouter router)
                router.Failed += (_, reason) => _post(() =>
                {
                    _loadTimer?.Dispose();
                    IsLoading = false;
                    Failure = reason;
                    Console.WriteLine($"[Playback] {Current?.Title}: {reason}");
                    Changed?.Invoke();
                });
            _engine.FileLoaded += (_, _) => _post(OnLoaded);
            _engine.StreamEnded += (_, _) => _post(OnEnded);
            _engine.PauseStateChanged += (_, paused) => _post(() => { IsPaused = paused; Changed?.Invoke(); });
            _engine.DurationChanged += (_, _) => _post(() => Changed?.Invoke());
        }

        /// <summary>Tune a channel. The ident can show it immediately; the tuner gets the channel you rest on.</summary>
        public void PlayLive(Channel channel)
        {
            SaveCurrent();
            ResetWatched();
            Current = new NowPlaying("live", channel.Id, channel.Name, channel.StreamUrl, channel);
            BehindLive = TimeSpan.Zero; _pausedAt = null; IsPaused = false; Failure = null; IsLoading = true; IsAudioOnly = false; _saved = false; _quickDrops = 0;
            _tuneTimer?.Dispose(); _loadTimer?.Dispose();
            _tuneTimer = _timers.After(TuneDebounce, () => Start(channel.StreamUrl, null));
            Changed?.Invoke();
        }

        public void PlayItem(string itemId, string url, TimeSpan resume, string title)
        {
            SaveCurrent();
            ResetWatched();
            _tuneTimer?.Dispose();
            Current = new NowPlaying("vod", itemId, title, url, null);
            IsAudioOnly = false;
            BehindLive = TimeSpan.Zero; _pausedAt = null; IsPaused = false; Failure = null; _saved = false;
            _loadTimer?.Dispose();
            Start(url, resume > TimeSpan.Zero ? resume.TotalSeconds : null);
        }

        private void Start(string url, double? start)
        {
            _retries = 0; _start = start;
            IsLoading = true;
            _configure?.Invoke(Current?.Kind == "live");
            Started?.Invoke();
            _engine.Play(url, start);
            // Browser services (Netflix, Disney+, ITVX, Channel 4) have their own timeouts and failures.
            if (_engine is EngineRouter r && r.BrowserFor(url) != null) _loadTimer?.Dispose();
            else ArmLoadTimer();
            Changed?.Invoke();
        }

        private void ArmLoadTimer()
        {
            _loadTimer?.Dispose();
            _loadTimer = _timers.After(LoadTimeout, () =>
            {
                if (!IsLoading || Current == null) return;
                if (_retries < MaxRetries)
                {
                    _retries++;
                    Console.WriteLine($"[Playback] {Current.Title} didn't load in {LoadTimeout.TotalSeconds:0} s; retry {_retries}/{MaxRetries}.");
                    _engine.Play(Current.Url, _start);
                    ArmLoadTimer();
                }
                else
                {
                    IsLoading = false;
                    Failure = $"{Current.Title} isn't loading. Try again, or another channel.";
                    Console.WriteLine($"[Playback] {Failure}");
                    Changed?.Invoke();
                }
            });
        }

        private void OnLoaded()
        {
            _loadTimer?.Dispose();
            IsLoading = false; Failure = null;
            IsAudioOnly = _engine.IsAudioOnly;
            _loadedAt = _now();
            // The minute counts from the first picture; a live stream that re-tunes keeps the same minute.
            if (_watchTimer == null && !_watched) _watchTimer = _timers.After(WatchedAfter, () => { _watched = true; Persist(); });
            _engine.SetSubtitle(SubtitlesOn); // each play gets the subtitles you chose
            Loaded?.Invoke();
            Changed?.Invoke();
        }

        private void OnEnded()
        {
            // A live stream has no end: if it drops, tune it again (unless it keeps dropping straight away).
            if (Current is { Kind: "live" } live)
            {
                _quickDrops = _now() - _loadedAt < QuickDrop ? _quickDrops + 1 : 0;
                if (_quickDrops < MaxQuickDrops)
                {
                    Console.WriteLine($"[Playback] {live.Title} stopped; tuning again.");
                    Start(live.Url, null);
                    return;
                }
                IsLoading = false;
                Failure = $"{live.Title} keeps stopping. Try again, or another channel.";
                Console.WriteLine($"[Playback] {Failure}");
                Changed?.Invoke();
                return;
            }
            _watched = true; // played to the end: it counts, however short
            SaveCurrent();
            Ended?.Invoke();
        }

        /// <summary>Play the current item again (after a failure was fixed, e.g. signing in).</summary>
        public void Replay()
        {
            if (Current == null) return;
            Failure = null;
            Start(Current.Url, Current.Kind == "live" ? null : _start);
        }

        /// <summary>Subtitles for what plays (starts from the Settings default; the info bar toggles it).</summary>
        public bool SubtitlesOn { get; private set; } = true;

        /// <summary>Settings: subtitles on or off by default. Setting it also sets what plays from now on.</summary>
        public bool SubtitlesDefault
        {
            get => _subtitlesDefault;
            set { _subtitlesDefault = value; SubtitlesOn = value; if (Current != null && !IsLoading) _engine.SetSubtitle(value); }
        }
        private bool _subtitlesDefault = true;

        public void StartOver()
        {
            if (Current is not { Kind: "vod" }) return;
            _engine.Seek(0);
            Changed?.Invoke();
        }

        public void ToggleSubtitles()
        {
            if (Current == null) return;
            SubtitlesOn = !SubtitlesOn;
            _engine.SetSubtitle(SubtitlesOn);
            Changed?.Invoke();
        }

        public void TogglePause()
        {
            if (Current == null) return;
            if (IsPaused || _engine.State == PlaybackState.Paused)
            {
                if (_pausedAt is { } at && Current.Kind == "live") BehindLive += _now() - at;
                _pausedAt = null;
                _engine.Resume();
            }
            else
            {
                _pausedAt = _now();
                _engine.Pause();
            }
            Changed?.Invoke();
        }

        public void SeekRelative(TimeSpan delta)
        {
            if (Current == null) return;
            if (Current.Kind == "live")
            {
                // Can't go past live.
                var behind = BehindLive - delta;
                if (behind < TimeSpan.Zero) { delta += behind; behind = TimeSpan.Zero; }
                BehindLive = behind;
            }
            if (delta != TimeSpan.Zero) _engine.SeekRelative(delta.TotalSeconds);
            Changed?.Invoke();
        }

        public void Stop()
        {
            SaveCurrent();
            ResetWatched();
            _tuneTimer?.Dispose(); _loadTimer?.Dispose();
            _engine.Stop();
            Current = null; IsLoading = false; IsAudioOnly = false;
            Changed?.Invoke();
        }

        private void ResetWatched()
        {
            _watchTimer?.Dispose(); _watchTimer = null; _watched = false;
        }

        /// <summary>Writes the current position to history (channels too: they have no end, so no progress).</summary>
        private void Persist()
        {
            if (Current == null || _save == null) return;
            _save(Current.ItemId, Position, Current.Kind == "live" ? TimeSpan.Zero : Duration);
        }

        /// <summary>On leaving: update history, but only for something watched long enough (it was written when the minute passed).</summary>
        private void SaveCurrent()
        {
            if (Current == null || _saved || !_watched) return;
            _saved = true;
            Persist();
        }
    }
}
