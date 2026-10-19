using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Baird.Mpv;
using Baird.Services;
using ReactiveUI;

namespace Baird.ViewModels
{
    public class AvStatRowViewModel : ReactiveObject
    {
        public string Name { get; }

        private string _value = "–";
        public string Value
        {
            get => _value;
            set => this.RaiseAndSetIfChanged(ref _value, value);
        }

        private bool _isWarning;
        public bool IsWarning
        {
            get => _isWarning;
            set => this.RaiseAndSetIfChanged(ref _isWarning, value);
        }

        public AvStatRowViewModel(string name) => Name = name;
    }

    public class AvStatGroupViewModel
    {
        public string Title { get; }
        public ObservableCollection<AvStatRowViewModel> Rows { get; } = new();

        public AvStatGroupViewModel(string title) => Title = title;
    }

    public class AvDebugEventViewModel
    {
        public DateTime Timestamp { get; init; }
        public string Message { get; init; } = string.Empty;
        public bool IsWarning { get; init; }

        public string TimeText => Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        public string Color => IsWarning ? "#FFB74D" : "#CCCCCC";
    }

    /// <summary>
    /// A/V timing debug page. Samples mpv + render-loop + system stats once a second while the page
    /// is visible, or continuously while journal logging is on (so you can watch TV normally and
    /// read the journal afterwards). Press M while watching, or Mark here, to flag "that felt off".
    /// </summary>
    public class AvDebugViewModel : ReactiveObject
    {
        // |avsync| above this is in the range people start to notice lip-sync error.
        private const double AvSyncWarnSeconds = 0.040;
        private const double LateRenderWarnMs = 20;
        private const double HotCelsius = 80;
        private const int MaxEvents = 200;
        private const double AudioDelayStepMs = 10;

        private readonly SystemMonitor _systemMonitor;
        private Avalonia.Threading.DispatcherTimer? _timer;
        private bool _sampling;
        private int _markCount;

        // Previous-sample state for change detection
        private AvStats? _last;
        private SystemStats? _lastSystem;
        private string? _lastThrottled;
        private bool _avSyncOutOfRange;

        // Counters are shown relative to this baseline (reset per file and by Reset Counters)
        private long _baseDecoderDrops, _baseVoDrops, _baseMistimed, _baseDelayed;

        public event EventHandler? BackRequested;

        /// <summary>Set by the view once the video player exists.</summary>
        public IAvDiagnosticsSource? Source { get; set; }

        /// <summary>Human-readable name of what's playing (channel/programme); stream URLs are opaque.</summary>
        public Func<string?>? CurrentTitle { get; set; }

        public ObservableCollection<AvStatGroupViewModel> Groups { get; } = new();
        public ObservableCollection<AvDebugEventViewModel> Events { get; } = new();
        private readonly Dictionary<string, AvStatRowViewModel> _rows = new();

        private bool _isPageVisible;
        public bool IsPageVisible
        {
            get => _isPageVisible;
            set
            {
                this.RaiseAndSetIfChanged(ref _isPageVisible, value);
                UpdateTimer();
            }
        }

        private bool _isJournalLoggingEnabled;
        public bool IsJournalLoggingEnabled
        {
            get => _isJournalLoggingEnabled;
            set
            {
                this.RaiseAndSetIfChanged(ref _isJournalLoggingEnabled, value);
                this.RaisePropertyChanged(nameof(JournalLoggingText));
                Console.WriteLine($"[AvDebug] Journal logging {(value ? "on" : "off")}");
                UpdateTimer();
            }
        }

        public string JournalLoggingText => IsJournalLoggingEnabled ? "Journal logging: ON" : "Journal logging: off";

        private double _audioDelayMs;
        public double AudioDelayMs
        {
            get => _audioDelayMs;
            private set
            {
                this.RaiseAndSetIfChanged(ref _audioDelayMs, value);
                this.RaisePropertyChanged(nameof(AudioDelayText));
            }
        }

        public string AudioDelayText => AudioDelayMs switch
        {
            0 => "Audio delay: 0 ms",
            > 0 => $"Audio delay: +{AudioDelayMs:0} ms (audio later)",
            _ => $"Audio delay: {AudioDelayMs:0} ms (audio earlier)",
        };

        private string _statusText = "Not sampling";
        public string StatusText
        {
            get => _statusText;
            set => this.RaiseAndSetIfChanged(ref _statusText, value);
        }

        public ReactiveCommand<Unit, Unit> ToggleJournalLoggingCommand { get; }
        public ReactiveCommand<Unit, Unit> MarkCommand { get; }
        public ReactiveCommand<Unit, Unit> AudioDelayLaterCommand { get; }
        public ReactiveCommand<Unit, Unit> AudioDelayEarlierCommand { get; }
        public ReactiveCommand<Unit, Unit> AudioDelayResetCommand { get; }
        public ReactiveCommand<Unit, Unit> ResetCountersCommand { get; }
        public ReactiveCommand<Unit, Unit> ClearEventsCommand { get; }
        public ReactiveCommand<Unit, Unit> BackCommand { get; }

        public AvDebugViewModel(SystemMonitor? systemMonitor = null)
        {
            _systemMonitor = systemMonitor ?? new SystemMonitor();
            BuildRows();

            ToggleJournalLoggingCommand = ReactiveCommand.Create(() => { IsJournalLoggingEnabled = !IsJournalLoggingEnabled; });
            MarkCommand = ReactiveCommand.Create(Mark);
            AudioDelayLaterCommand = ReactiveCommand.Create(() => SetAudioDelay(AudioDelayMs + AudioDelayStepMs));
            AudioDelayEarlierCommand = ReactiveCommand.Create(() => SetAudioDelay(AudioDelayMs - AudioDelayStepMs));
            AudioDelayResetCommand = ReactiveCommand.Create(() => SetAudioDelay(0));
            ResetCountersCommand = ReactiveCommand.Create(ResetCounters);
            ClearEventsCommand = ReactiveCommand.Create(() => Events.Clear());
            BackCommand = ReactiveCommand.Create(() => BackRequested?.Invoke(this, EventArgs.Empty));
        }

        private void BuildRows()
        {
            void Group(string title, params string[] rows)
            {
                var group = new AvStatGroupViewModel(title);
                foreach (var name in rows)
                {
                    var row = new AvStatRowViewModel(name);
                    _rows[name] = row;
                    group.Rows.Add(row);
                }
                Groups.Add(group);
            }

            Group("Sync", "A/V offset", "Total A/V correction", "Audio delay", "Video sync", "Display sync");
            Group("Frames (since reset)", "Decoder drops", "VO drops", "Delayed", "Mistimed");
            Group("Render loop (last second)", "Renders / callbacks", "Frame interval", "Render call", "Callback → render", "Late renders");
            Group("Display", "Display fps (est.)", "Vsync jitter", "Video fps (est. / container)");
            Group("Source", "State", "File", "Video", "Hardware decode", "Filters / deinterlace", "Audio");
            Group("Buffer", "Cache ahead", "Buffering");
            Group("System", "Process CPU", "Top threads", "CPU temp", "ARM clock", "Throttled");
        }

        private void Set(string row, string value, bool warn = false)
        {
            var r = _rows[row];
            r.Value = value;
            r.IsWarning = warn;
        }

        private void UpdateTimer()
        {
            bool active = IsPageVisible || IsJournalLoggingEnabled;
            if (active)
            {
                if (_timer == null)
                {
                    _timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _timer.Tick += async (s, e) =>
                    {
                        try
                        {
                            await SampleAsync();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[AvDebug] Sample failed: {ex.Message}");
                        }
                    };
                }
                _timer.Start();
                StatusText = IsJournalLoggingEnabled ? "Sampling every 1s (logging to journal)" : "Sampling every 1s";
            }
            else
            {
                _timer?.Stop();
                StatusText = "Not sampling";
            }
        }

        public async Task SampleAsync()
        {
            if (_sampling) return; // previous sample still waiting on the mpv command thread
            _sampling = true;
            try
            {
                var stats = Source == null ? null : await Source.SampleAvStatsAsync();
                var system = _systemMonitor.Sample();
                Apply(stats, system);
            }
            finally
            {
                _sampling = false;
            }
        }

        private void Apply(AvStats? s, SystemStats sys)
        {
            if (s != null)
            {
                DetectEvents(s, sys);
                ApplyAvStats(s);
            }
            ApplySystem(sys);

            if (IsJournalLoggingEnabled)
                Console.WriteLine($"[AvDebug] {FormatLogLine(s, sys)}");

            _last = s;
            _lastSystem = sys;
            _lastThrottled = sys.Throttled;
        }

        private void ApplyAvStats(AvStats s)
        {
            bool avsyncWarn = s.AvSyncSeconds is double a && Math.Abs(a) > AvSyncWarnSeconds;
            Set("A/V offset", Ms(s.AvSyncSeconds), avsyncWarn);
            Set("Total A/V correction", Ms(s.TotalAvSyncChangeSeconds));
            Set("Audio delay", Ms(s.AudioDelaySeconds));
            Set("Video sync", s.VideoSyncMode ?? "–");
            bool wantsDisplaySync = s.VideoSyncMode?.StartsWith("display", StringComparison.Ordinal) == true;
            Set("Display sync", s.DisplaySyncActive switch { true => "active", false => "inactive", null => "–" },
                wantsDisplaySync && s.DisplaySyncActive == false && s.State != PlaybackState.Idle);

            long dec = (s.DecoderDrops ?? 0) - _baseDecoderDrops;
            long vo = (s.VoDrops ?? 0) - _baseVoDrops;
            long delayed = (s.DelayedFrames ?? 0) - _baseDelayed;
            long mistimed = (s.MistimedFrames ?? 0) - _baseMistimed;
            Set("Decoder drops", dec.ToString(CultureInfo.InvariantCulture), dec > 0);
            Set("VO drops", vo.ToString(CultureInfo.InvariantCulture), vo > 0);
            Set("Delayed", delayed.ToString(CultureInfo.InvariantCulture), delayed > 0);
            Set("Mistimed", mistimed.ToString(CultureInfo.InvariantCulture));

            var r = s.Render;
            Set("Renders / callbacks", $"{r.Renders} / {r.UpdateCallbacks}");
            Set("Frame interval", r.Renders > 1 ? $"avg {r.IntervalAvgMs:0.0} ms, max {r.IntervalMaxMs:0.0} ms" : "–");
            Set("Render call", r.Renders > 0 ? $"avg {r.RenderAvgMs:0.0} ms, max {r.RenderMaxMs:0.0} ms" : "–");
            Set("Callback → render", r.Renders > 0 ? $"avg {r.LatencyAvgMs:0.0} ms, max {r.LatencyMaxMs:0.0} ms" : "–",
                r.LatencyMaxMs > LateRenderWarnMs);
            Set("Late renders", $"{r.LateRenders} (> {LateRenderWarnMs:0} ms waiting)", r.LateRenders > 0);

            Set("Display fps (est.)", Num(s.EstimatedDisplayFps, "0.000"));
            Set("Vsync jitter", Num(s.VsyncJitter, "0.000"));
            Set("Video fps (est. / container)", $"{Num(s.EstimatedVideoFps, "0.00")} / {Num(s.ContainerFps, "0.00")}");

            Set("State", s.State.ToString());
            Set("File", Title(s.Path));
            Set("Video", s.VideoCodec == null ? "–" : $"{s.VideoCodec} {s.Width}x{s.Height} {s.PixelFormat}");
            Set("Hardware decode", s.HwdecCurrent ?? "–");
            Set("Filters / deinterlace", $"vf=[{s.VideoFilters}] deinterlace={(s.DeinterlaceActive switch { true => "active", false => "off", null => "?" })}");
            Set("Audio", s.AudioCodec == null ? "–" : $"{s.AudioCodec} {s.AudioSampleRate} Hz via {s.AudioOutput}");

            Set("Cache ahead", s.CacheSeconds is double c ? $"{c:0.0} s" : "–");
            Set("Buffering", s.PausedForCache == true ? "yes" : "no", s.PausedForCache == true);
        }

        private void ApplySystem(SystemStats sys)
        {
            Set("Process CPU", sys.ProcessCpuPercent is double cpu ? $"{cpu:0}% (of one core)" : "–");
            Set("Top threads", sys.TopThreads.Count == 0 ? "–" : string.Join("  ", sys.TopThreads.Select(t => $"{t.Name} {t.Percent:0}%")));
            Set("CPU temp", sys.CpuTempCelsius is double t ? $"{t:0.0} °C" : "–", sys.CpuTempCelsius > HotCelsius);
            Set("ARM clock", sys.ArmClockMhz is double mhz ? $"{mhz:0} MHz" : "–");
            Set("Throttled", sys.Throttled ?? "–", sys.Throttled != null && sys.Throttled != "0x0");
        }

        private void DetectEvents(AvStats s, SystemStats sys)
        {
            // New file: mpv resets its counters, so reset our baseline too.
            if (_last == null || s.Path != _last.Path)
            {
                _baseDecoderDrops = _baseVoDrops = _baseMistimed = _baseDelayed = 0;
                if (!string.IsNullOrEmpty(s.Path)) AddEvent($"Playing: {Title(s.Path)}");
            }
            else
            {
                AddDeltaEvent("decoder drop", s.DecoderDrops, _last.DecoderDrops);
                AddDeltaEvent("VO drop", s.VoDrops, _last.VoDrops);
                AddDeltaEvent("delayed frame", s.DelayedFrames, _last.DelayedFrames);
            }

            if (s.AvSyncSeconds is double a)
            {
                bool outOfRange = Math.Abs(a) > AvSyncWarnSeconds;
                if (outOfRange && !_avSyncOutOfRange) AddEvent($"A/V offset {Ms(a)} (beyond ±{AvSyncWarnSeconds * 1000:0} ms)", warn: true);
                else if (!outOfRange && _avSyncOutOfRange) AddEvent($"A/V offset back in range ({Ms(a)})");
                _avSyncOutOfRange = outOfRange;
            }

            if (s.Render.LateRenders > 0)
                AddEvent($"{s.Render.LateRenders} late render(s), frame waited up to {s.Render.LatencyMaxMs:0} ms", warn: true);

            if (s.PausedForCache == true && _last?.PausedForCache != true) AddEvent("Buffering (paused for cache)", warn: true);
            else if (s.PausedForCache == false && _last?.PausedForCache == true) AddEvent("Buffering finished");

            if (sys.Throttled != null && _lastThrottled != null && sys.Throttled != _lastThrottled)
                AddEvent($"Throttle flags changed: {_lastThrottled} → {sys.Throttled}", warn: sys.Throttled != "0x0");
        }

        private void AddDeltaEvent(string what, long? now, long? before)
        {
            long delta = (now ?? 0) - (before ?? 0);
            if (delta > 0) AddEvent($"+{delta} {what}{(delta == 1 ? "" : "s")}", warn: true);
        }

        private void AddEvent(string message, bool warn = false)
        {
            Events.Insert(0, new AvDebugEventViewModel { Timestamp = DateTime.Now, Message = message, IsWarning = warn });
            while (Events.Count > MaxEvents) Events.RemoveAt(Events.Count - 1);
            if (IsJournalLoggingEnabled || warn)
                Console.WriteLine($"[AvDebug] EVENT {message}");
        }

        /// <summary>Flags "that felt off" with a full snapshot in the journal.</summary>
        public void Mark()
        {
            _markCount++;
            AddEvent($"MARK #{_markCount} — user flagged an issue", warn: true);
            Console.WriteLine($"[AvDebug] MARK #{_markCount} {FormatLogLine(_last, _lastSystem ?? _systemMonitor.Sample())}");
        }

        private void SetAudioDelay(double ms)
        {
            AudioDelayMs = Math.Round(ms);
            Source?.SetAudioDelay(AudioDelayMs / 1000.0);
            AddEvent($"Audio delay set to {AudioDelayText["Audio delay: ".Length..]}");
        }

        private void ResetCounters()
        {
            if (_last != null)
            {
                _baseDecoderDrops = _last.DecoderDrops ?? 0;
                _baseVoDrops = _last.VoDrops ?? 0;
                _baseMistimed = _last.MistimedFrames ?? 0;
                _baseDelayed = _last.DelayedFrames ?? 0;
            }
            AddEvent("Counters reset");
        }

        private string Title(string? path)
        {
            var title = CurrentTitle?.Invoke();
            return string.IsNullOrEmpty(title) ? ShortName(path) : $"{title} ({ShortName(path)})";
        }

        private string FormatLogLine(AvStats? s, SystemStats sys)
        {
            var top = string.Join(",", sys.TopThreads.Select(t => $"{t.Name.Replace(' ', '_')}:{t.Percent:0}"));
            if (s == null)
                return $"no-player cpu={sys.ProcessCpuPercent:0}% temp={sys.CpuTempCelsius:0.0}C arm={sys.ArmClockMhz:0}MHz throttled={sys.Throttled} threads={top}";

            var r = s.Render;
            return string.Create(CultureInfo.InvariantCulture,
                $"state={s.State} file=\"{Title(s.Path)}\" avsync={Ms(s.AvSyncSeconds)} avchange={Ms(s.TotalAvSyncChangeSeconds)} " +
                $"adelay={Ms(s.AudioDelaySeconds)} sync={s.VideoSyncMode} dsync={s.DisplaySyncActive} " +
                $"drops(dec/vo)={s.DecoderDrops}/{s.VoDrops} delayed={s.DelayedFrames} mistimed={s.MistimedFrames} " +
                $"dfps={s.EstimatedDisplayFps:0.000} jitter={s.VsyncJitter:0.000} vfps={s.EstimatedVideoFps:0.00} " +
                $"renders={r.Renders}/{r.UpdateCallbacks} interval={r.IntervalAvgMs:0.0}/{r.IntervalMaxMs:0.0}ms " +
                $"render={r.RenderAvgMs:0.0}/{r.RenderMaxMs:0.0}ms cb2render={r.LatencyAvgMs:0.0}/{r.LatencyMaxMs:0.0}ms late={r.LateRenders} " +
                $"hwdec={s.HwdecCurrent} vf=[{s.VideoFilters}] cache={s.CacheSeconds:0.0}s buffering={s.PausedForCache} " +
                $"cpu={sys.ProcessCpuPercent:0}% temp={sys.CpuTempCelsius:0.0}C arm={sys.ArmClockMhz:0}MHz throttled={sys.Throttled} threads={top}");
        }

        private static string Ms(double? seconds) =>
            seconds is double v ? (v * 1000).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " ms" : "–";

        private static string Num(double? value, string format) =>
            value is double v ? v.ToString(format, CultureInfo.InvariantCulture) : "–";

        private static string ShortName(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "–";
            var noQuery = path.Split('?')[0].TrimEnd('/');
            int slash = noQuery.LastIndexOf('/');
            return slash >= 0 ? noQuery[(slash + 1)..] : noQuery;
        }
    }
}
