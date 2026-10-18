using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Baird.Services;

namespace Baird.Mpv
{
    public enum PlaybackState { Idle, Loading, Playing, Paused, Buffering }

    /// <summary>
    /// Wraps a libmpv core plus its OpenGL render context.
    ///
    /// Threading model (see libmpv render.h "Threading"): the thread that calls Render() — Avalonia's
    /// UI thread — must not make blocking libmpv calls, or the core and the renderer can wait on each
    /// other ("mpv_render_context_render() not being called or stuck"). So:
    ///   - commands and property writes are queued to a dedicated MpvCommands thread (in order);
    ///   - property reads are served from values cached by mpv_observe_property on the event thread;
    ///   - only mpv_render_* functions are called from the render thread.
    /// </summary>
    public class MpvPlayer : IDisposable, IAvDiagnosticsSource
    {
        // Observe-property reply ids
        private const ulong ObservePause = 1;
        private const ulong ObserveDuration = 2;
        private const ulong ObserveTimePos = 3;
        private const ulong ObservePath = 4;

        private IntPtr _mpvHandle;
        private IntPtr _renderContext;
        private System.Threading.Thread? _eventThread;
        private System.Threading.Thread? _commandThread;
        private readonly BlockingCollection<Action> _commandQueue = new();
        private volatile bool _eventLoopRunning;
        private volatile bool _disposed;
        private Action? _requestRender;

        // Render parameters, allocated once in InitializeOpenGl and reused every frame.
        private IntPtr _pFbo;
        private IntPtr _pFlipY;
        private LibMpv.MpvRenderParam[]? _renderParams;

        // Values cached from property observation (written on the event thread).
        private volatile bool _isMpvPaused;
        private double _timePosSeconds;
        private double _durationSeconds;
        private volatile string? _currentPath;

        // Video-quality feature flags (toggled live from the Settings page).
        private bool _isLive;                 // last source type, so deinterlace can be re-applied on toggle
        private bool _highQualityScaling;     // spline36 scaler vs bilinear
        private bool _sharperDeinterlacing;   // bwdif vs default yadif
        private bool _logRenderDimensions;    // log the FBO size handed to mpv each time it changes
        private int _lastLoggedW = -1;
        private int _lastLoggedH = -1;

        // Render-loop timing for the A/V debug page (written on the render thread, read on the
        // command thread when sampled). Intervals longer than this are idle gaps, not frames.
        private const double MaxFrameIntervalMs = 500;
        // One frame at 50 Hz: a frame kept waiting longer than this was likely shown late.
        private const double LateRenderThresholdMs = 20;
        private readonly object _timingLock = new();
        private long _pendingCallbackTimestamp; // first update callback not yet rendered, 0 = none
        private int _updateCallbacks;
        private long _lastRenderStart;
        private int _renders, _intervalCount, _lateRenders;
        private double _intervalSumMs, _intervalMaxMs, _renderSumMs, _renderMaxMs, _latencySumMs, _latencyMaxMs;

        private LibMpv.MpvRenderUpdateFn? _renderUpdateFn;
        public event EventHandler? StreamEnded;
        /// <summary>Fired when mpv reports a load/playback error. Arg is the mpv error code.</summary>
        public event EventHandler<int>? StreamLoadFailed;
        /// <summary>Fired when a new file has finished loading and playback begins.</summary>
        public event EventHandler? FileLoaded;
        /// <summary>Fired when mpv's pause state changes. Arg is true when paused.</summary>
        public event EventHandler<bool>? PauseStateChanged;
        /// <summary>Fired when the file duration becomes known or changes. Arg is seconds.</summary>
        public event EventHandler<double>? DurationChanged;

        private volatile PlaybackState _state = PlaybackState.Idle;
        public PlaybackState State
        {
            get => _state;
            private set => _state = value;
        }

        // Cached reads — safe (and non-blocking) to call from the UI/render thread.
        public double TimePositionSeconds => Volatile.Read(ref _timePosSeconds);
        public double DurationSeconds => Volatile.Read(ref _durationSeconds);
        public string CurrentPath => _currentPath ?? "None";

        public MpvPlayer()
        {
            _mpvHandle = LibMpv.mpv_create();
            if (_mpvHandle == IntPtr.Zero)
                throw new Exception("Failed to create mpv context");

            // Options before mpv_initialize are set synchronously; nothing is rendering yet.

            // Hardware acceleration: v4l2m2m-copy uses the Pi 5's rpi-hevc-dec kernel module
            // for HEVC content via /dev/video19, with frame copy-back for OpenGL compositing.
            // Falls back to software decode for codecs without a V4L2 M2M driver (e.g. H.264,
            // which has no rpi_h264_dec on this kernel). "auto-copy" tried VA-API first but
            // the Pi 5's V3D GPU has no VA-API driver, wasting time before falling back.
            Console.WriteLine("[MpvPlayer] Hardware decoding mode: v4l2m2m-copy");
            SetOption("hwdec", "v4l2m2m-copy");

            // Deinterlace is configured per-source via ConfigureForSource() before each Play().
            // Default off; enabled for live broadcast streams (1080i25 Freesat).

            // Generics Options
            // mpv's own log output is routed through the event loop (LogMessage) so stream URLs
            // carrying credentials can be redacted; writing to the terminal directly would leak them.
            SetOption("terminal", "no");

            // Disable default input bindings and media keys as Avalonia handles all user input
            SetOption("input-default-bindings", "no");
            SetOption("input-media-keys", "no");
            SetOption("input-vo-keyboard", "no");

            // Critical for embedding in Avalonia/OpenGL: "libmpv" forces mpv to use the
            // render API and prevents a detached window.  For standalone (non-embedded) usage
            // on Raspberry Pi you can switch this to "drm" for direct KMS/DRM rendering,
            // but that will bypass the OpenGL render context below and won't work in this
            // embedded configuration.
            SetOption("vo", "libmpv");

            // Maintain aspect ratio (will center with black bars if needed)
            SetOption("keepaspect", "yes");

            // Audio filter and deinterlace are set per-source by ConfigureForSource().
            // Volume is always software in current mpv (the old "softvol" option is gone).
            SetOption("volume-max", "200");
            SetOption("volume", "100");
            // display-vdrop: drops one frame per display-cycle boundary when content and display
            // rates are slightly misaligned. Requires mpv_render_context_report_swap() after
            // each render (done in Render()) to give mpv accurate display timing feedback.
            // Better than video-sync=audio for embedded rendering because audio clock drift
            // on live streams no longer causes periodic frame-delivery misses.
            SetOption("video-sync", "display-vdrop");

            // Prefer English audio
            SetOption("alang", "eng,en");
            // Prefer English subtitles
            SetOption("slang", "eng,en");

            var res = LibMpv.mpv_initialize(_mpvHandle);
            if (res < 0)
                throw new Exception($"Failed to initialize mpv: {ErrorString(res)}");

            LibMpv.mpv_request_log_messages(_mpvHandle, "warn");

            // Observe properties so the event loop can cache values and raise typed C# events
            // instead of the UI thread polling the core.
            LibMpv.mpv_observe_property(_mpvHandle, ObservePause, "pause", LibMpv.MpvFormat.Flag);
            LibMpv.mpv_observe_property(_mpvHandle, ObserveDuration, "duration", LibMpv.MpvFormat.Double);
            LibMpv.mpv_observe_property(_mpvHandle, ObserveTimePos, "time-pos", LibMpv.MpvFormat.Double);
            LibMpv.mpv_observe_property(_mpvHandle, ObservePath, "path", LibMpv.MpvFormat.String);

            _eventLoopRunning = true;
            _eventThread = new System.Threading.Thread(EventLoop)
            {
                IsBackground = true,
                Name = "MpvEventLoop"
            };
            _eventThread.Start();

            _commandThread = new System.Threading.Thread(CommandLoop)
            {
                IsBackground = true,
                Name = "MpvCommands"
            };
            _commandThread.Start();
        }

        private void CommandLoop()
        {
            foreach (var action in _commandQueue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MpvPlayer] Error executing queued command: {ex.Message}");
                }
            }
        }

        /// <summary>Queues work for the MpvCommands thread. Order of posts is preserved.</summary>
        private void Post(Action action)
        {
            if (_disposed) return;
            try
            {
                _commandQueue.Add(action);
            }
            catch (InvalidOperationException)
            {
                // Queue completed during Dispose; drop the command.
            }
        }

        private void EventLoop()
        {
            Console.WriteLine("[MpvPlayer] Event loop thread started");

            while (_eventLoopRunning)
            {
                try
                {
                    // Wait for events with 1 second timeout (Dispose wakes us early via mpv_wakeup)
                    IntPtr eventPtr = LibMpv.mpv_wait_event(_mpvHandle, 1.0);
                    if (eventPtr == IntPtr.Zero)
                        continue;

                    var evt = Marshal.PtrToStructure<LibMpv.MpvEvent>(eventPtr);

                    if (evt.EventId == LibMpv.MpvEventId.EndFile)
                    {
                        if (evt.Data != IntPtr.Zero)
                        {
                            var endFileEvent = Marshal.PtrToStructure<LibMpv.MpvEndFileEvent>(evt.Data);
                            Console.WriteLine($"[MpvPlayer] EndFile event: reason={endFileEvent.Reason}, error={endFileEvent.Error}");

                            if (endFileEvent.Reason == LibMpv.MpvEndFileReason.Eof)
                            {
                                Console.WriteLine("[MpvPlayer] Stream ended naturally (EOF)");
                                StreamEnded?.Invoke(this, EventArgs.Empty);
                            }
                            else if (endFileEvent.Reason == LibMpv.MpvEndFileReason.Error)
                            {
                                Console.WriteLine($"[MpvPlayer] Stream failed: {ErrorString(endFileEvent.Error)}");
                                State = PlaybackState.Idle;
                                StreamLoadFailed?.Invoke(this, endFileEvent.Error);
                            }
                            else if (endFileEvent.Reason == LibMpv.MpvEndFileReason.Redirect)
                            {
                                // Not a real failure; mpv will re-open the redirected URL automatically
                                Console.WriteLine("[MpvPlayer] EndFile: redirect, ignoring");
                            }
                            // Stop/Quit: state will be reset by the caller
                        }
                    }
                    else if (evt.EventId == LibMpv.MpvEventId.FileLoaded)
                    {
                        Console.WriteLine("[MpvPlayer] FileLoaded event received");
                        // A pause requested while loading must survive the load completing.
                        State = _isMpvPaused ? PlaybackState.Paused : PlaybackState.Playing;
                        // Safe here: this is not the render thread.
                        LogAudioTracks();
                        FileLoaded?.Invoke(this, EventArgs.Empty);
                    }
                    else if (evt.EventId == LibMpv.MpvEventId.PropertyChange)
                    {
                        if (evt.Data != IntPtr.Zero)
                            HandlePropertyChange(evt.ReplyUserdata, Marshal.PtrToStructure<LibMpv.MpvEventProperty>(evt.Data));
                    }
                    else if (evt.EventId == LibMpv.MpvEventId.LogMessage)
                    {
                        if (evt.Data != IntPtr.Zero)
                        {
                            var msg = Marshal.PtrToStructure<LibMpv.MpvEventLogMessage>(evt.Data);
                            var prefix = Marshal.PtrToStringUTF8(msg.Prefix);
                            var text = Marshal.PtrToStringUTF8(msg.Text)?.TrimEnd('\n');
                            Console.WriteLine($"[mpv/{prefix}] {SecretRedactor.Redact(text)}");
                        }
                    }
                    else if (evt.EventId == LibMpv.MpvEventId.Shutdown)
                    {
                        Console.WriteLine("[MpvPlayer] Received shutdown event");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MpvPlayer] Error in event loop: {ex.Message}");
                }
            }

            Console.WriteLine("[MpvPlayer] Event loop thread exiting");
        }

        private void HandlePropertyChange(ulong id, LibMpv.MpvEventProperty prop)
        {
            // Format None means the property is currently unavailable (e.g. no file loaded).
            bool hasValue = prop.Format != LibMpv.MpvFormat.None && prop.Data != IntPtr.Zero;

            switch (id)
            {
                case ObservePause when hasValue:
                    bool isPaused = Marshal.ReadInt32(prop.Data) != 0;
                    _isMpvPaused = isPaused;
                    if (isPaused)
                    {
                        if (State != PlaybackState.Loading && State != PlaybackState.Idle)
                            State = PlaybackState.Paused;
                    }
                    else if (State == PlaybackState.Paused)
                    {
                        State = PlaybackState.Playing;
                    }
                    PauseStateChanged?.Invoke(this, isPaused);
                    break;

                case ObserveDuration:
                    double dur = hasValue ? Marshal.PtrToStructure<double>(prop.Data) : 0;
                    Volatile.Write(ref _durationSeconds, dur);
                    if (hasValue) DurationChanged?.Invoke(this, dur);
                    break;

                case ObserveTimePos:
                    Volatile.Write(ref _timePosSeconds, hasValue ? Marshal.PtrToStructure<double>(prop.Data) : 0);
                    break;

                case ObservePath:
                    _currentPath = hasValue ? Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(prop.Data)) : null;
                    break;
            }
        }

        public void InitializeOpenGl(IntPtr procAddressCallback, Action requestRender)
        {
            _requestRender = requestRender;

            // 1. Wrap the Avalonia proc address callback. The caller keeps its delegate alive;
            //    we pass the raw pointer through rather than re-wrapping it.
            var openglParams = new LibMpv.MpvOpenglInitParams
            {
                GetProcAddress = procAddressCallback,
                UserData = IntPtr.Zero,
                ExtraParams = IntPtr.Zero
            };

            // 2. Prepare the init params
            IntPtr openglParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(openglParams));
            Marshal.StructureToPtr(openglParams, openglParamsPtr, false);
            IntPtr apiType = Marshal.StringToHGlobalAnsi("opengl");

            var initParams = new LibMpv.MpvRenderParam[]
            {
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.ApiType, Data = apiType },
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.InitParams, Data = openglParamsPtr },
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.Invalid, Data = IntPtr.Zero }
            };

            // 3. Create the context
            int res;
            try
            {
                res = LibMpv.mpv_render_context_create(out _renderContext, _mpvHandle, initParams);
            }
            finally
            {
                // 4. Cleanup
                Marshal.FreeHGlobal(openglParamsPtr);
                Marshal.FreeHGlobal(apiType);
            }

            if (res < 0) throw new Exception($"Failed to create render context: {ErrorString(res)}");

            // 5. Per-frame render params, allocated once and reused
            _pFbo = Marshal.AllocHGlobal(Marshal.SizeOf<LibMpv.MpvOpenglFbo>());
            _pFlipY = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(_pFlipY, 1);
            _renderParams = new LibMpv.MpvRenderParam[]
            {
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.Fbo, Data = _pFbo },
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.FlipY, Data = _pFlipY },
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.Invalid, Data = IntPtr.Zero }
            };

            // 6. Set update callback
            _renderUpdateFn = UpdateCallback;
            LibMpv.mpv_render_context_set_update_callback(_renderContext, _renderUpdateFn, IntPtr.Zero);
        }

        private void UpdateCallback(IntPtr ctx)
        {
            Interlocked.Increment(ref _updateCallbacks);
            Interlocked.CompareExchange(ref _pendingCallbackTimestamp, Stopwatch.GetTimestamp(), 0);
            _requestRender?.Invoke();
        }

        public void Render(int fbo, int width, int height)
        {
            if (_disposed || _renderContext == IntPtr.Zero || _renderParams == null) return;

            var fboParam = new LibMpv.MpvOpenglFbo { Fbo = fbo, W = width, H = height, InternalFormat = 0 };
            Marshal.StructureToPtr(fboParam, _pFbo, false);

            // Lead 2 diagnostic: confirm the FBO mpv renders into is actually display-native
            // (1920x1080). If it is smaller, Avalonia/GL upscales it afterwards = extra blur.
            if (_logRenderDimensions && (width != _lastLoggedW || height != _lastLoggedH))
            {
                _lastLoggedW = width;
                _lastLoggedH = height;
                Console.WriteLine($"[MpvPlayer] Render FBO dimensions: {width}x{height}");
            }

            long start = Stopwatch.GetTimestamp();
            long callbackAt = Interlocked.Exchange(ref _pendingCallbackTimestamp, 0);

            LibMpv.mpv_render_context_render(_renderContext, _renderParams);
            // Report the swap so mpv gets accurate display timing feedback.
            // Required for display-sync modes (display-vdrop, display-resample) to work correctly.
            LibMpv.mpv_render_context_report_swap(_renderContext);

            RecordRenderTiming(start, Stopwatch.GetTimestamp(), callbackAt);
        }

        private void RecordRenderTiming(long start, long end, long callbackAt)
        {
            lock (_timingLock)
            {
                _renders++;
                double renderMs = Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
                _renderSumMs += renderMs;
                _renderMaxMs = Math.Max(_renderMaxMs, renderMs);

                if (_lastRenderStart != 0)
                {
                    double intervalMs = Stopwatch.GetElapsedTime(_lastRenderStart, start).TotalMilliseconds;
                    if (intervalMs < MaxFrameIntervalMs)
                    {
                        _intervalCount++;
                        _intervalSumMs += intervalMs;
                        _intervalMaxMs = Math.Max(_intervalMaxMs, intervalMs);
                    }
                }
                _lastRenderStart = start;

                if (callbackAt != 0)
                {
                    double latencyMs = Stopwatch.GetElapsedTime(callbackAt, start).TotalMilliseconds;
                    _latencySumMs += latencyMs;
                    _latencyMaxMs = Math.Max(_latencyMaxMs, latencyMs);
                    if (latencyMs > LateRenderThresholdMs) _lateRenders++;
                }
            }
        }

        /// <summary>Returns render timing since the previous call and starts a new window.</summary>
        private RenderTiming TakeRenderTiming()
        {
            lock (_timingLock)
            {
                int callbacks = Interlocked.Exchange(ref _updateCallbacks, 0);
                var timing = new RenderTiming(
                    Renders: _renders,
                    UpdateCallbacks: callbacks,
                    IntervalAvgMs: _intervalCount > 0 ? _intervalSumMs / _intervalCount : 0,
                    IntervalMaxMs: _intervalMaxMs,
                    RenderAvgMs: _renders > 0 ? _renderSumMs / _renders : 0,
                    RenderMaxMs: _renderMaxMs,
                    LatencyAvgMs: _renders > 0 ? _latencySumMs / _renders : 0,
                    LatencyMaxMs: _latencyMaxMs,
                    LateRenders: _lateRenders);
                _renders = _intervalCount = _lateRenders = 0;
                _intervalSumMs = _intervalMaxMs = _renderSumMs = _renderMaxMs = _latencySumMs = _latencyMaxMs = 0;
                return timing;
            }
        }

        public Task<AvStats?> SampleAvStatsAsync()
        {
            var tcs = new TaskCompletionSource<AvStats?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_disposed) return Task.FromResult<AvStats?>(null);
            // Read on the command thread: these are blocking libmpv calls.
            Post(() =>
            {
                try { tcs.TrySetResult(ReadAvStats()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            if (_disposed) tcs.TrySetResult(null); // queue closed before the read was accepted
            return tcs.Task;
        }

        private AvStats ReadAvStats()
        {
            return new AvStats
            {
                State = State,
                Path = _currentPath == null ? null : SecretRedactor.Redact(_currentPath),
                AvSyncSeconds = GetDouble("avsync"),
                TotalAvSyncChangeSeconds = GetDouble("total-avsync-change"),
                AudioDelaySeconds = GetDouble("audio-delay"),
                DisplaySyncActive = GetFlag("display-sync-active"),
                VideoSyncMode = GetPropertyString("video-sync"),
                DecoderDrops = GetLong("decoder-frame-drop-count"),
                VoDrops = GetLong("frame-drop-count"),
                MistimedFrames = GetLong("mistimed-frame-count"),
                DelayedFrames = GetLong("vo-delayed-frame-count"),
                EstimatedDisplayFps = GetDouble("estimated-display-fps"),
                VsyncJitter = GetDouble("vsync-jitter"),
                EstimatedVideoFps = GetDouble("estimated-vf-fps"),
                ContainerFps = GetDouble("container-fps"),
                VideoCodec = GetPropertyString("video-codec"),
                HwdecCurrent = GetPropertyString("hwdec-current"),
                PixelFormat = GetPropertyString("video-params/pixelformat"),
                Width = GetLong("video-params/w"),
                Height = GetLong("video-params/h"),
                VideoFilters = GetPropertyString("vf"),
                DeinterlaceActive = GetFlag("deinterlace-active"),
                AudioCodec = GetPropertyString("audio-codec-name"),
                AudioSampleRate = GetLong("audio-params/samplerate"),
                AudioOutput = GetPropertyString("current-ao"),
                CacheSeconds = GetDouble("demuxer-cache-duration"),
                PausedForCache = GetFlag("paused-for-cache"),
                Render = TakeRenderTiming(),
            };
        }

        /// <summary>Shifts audio relative to video. Positive = audio later. Persists across files.</summary>
        public void SetAudioDelay(double seconds)
        {
            SetPropertyString("audio-delay", seconds.ToString("0.000", CultureInfo.InvariantCulture));
            Console.WriteLine($"[MpvPlayer] Audio delay set to {seconds * 1000:+0;-0;0} ms");
        }

        private double? GetDouble(string name) =>
            double.TryParse(GetPropertyString(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        private long? GetLong(string name) =>
            long.TryParse(GetPropertyString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        private bool? GetFlag(string name) => GetPropertyString(name) switch
        {
            "yes" => true,
            "no" => false,
            _ => null,
        };

        public void ConfigureForSource(bool isLive)
        {
            _isLive = isLive;
            ApplyDeinterlace();
            // loudnorm is skipped on live — its ~1s look-ahead buffer causes AV desync on live
            // streams; latency is acceptable on pre-recorded VOD.
            SetPropertyString("af", isLive ? "" : "loudnorm=I=-15:TP=-1.5:LRA=11");
            Console.WriteLine($"[MpvPlayer] Configured for {(isLive ? "live" : "VOD")} source");
        }

        /// <summary>
        /// Applies the deinterlacer for the current source type. Live broadcast (1080i25) must be
        /// deinterlaced; VOD is progressive so it is left off (yadif would double CPU for nothing).
        /// When live, the filter is chosen by the "sharper deinterlacing" flag: bwdif (sharper) vs
        /// mpv's default yadif.
        /// </summary>
        private void ApplyDeinterlace()
        {
            if (_isLive)
            {
                if (_sharperDeinterlacing)
                {
                    // bwdif is an explicit vf; disable the built-in (yadif) toggle to avoid stacking.
                    SetPropertyString("deinterlace", "no");
                    SetPropertyString("vf", "bwdif");
                }
                else
                {
                    SetPropertyString("vf", "");
                    SetPropertyString("deinterlace", "yes");
                }
            }
            else
            {
                SetPropertyString("vf", "");
                SetPropertyString("deinterlace", "no");
            }
        }

        /// <summary>Lead 1: switch between bilinear and the higher-quality spline36 scaler
        /// (sharper upscale of the 1440x1080 anamorphic broadcast to 1920x1080).</summary>
        public void SetHighQualityScaling(bool enabled)
        {
            _highQualityScaling = enabled;
            if (enabled)
            {
                SetPropertyString("scale", "spline36");
                SetPropertyString("cscale", "spline36");
                SetPropertyString("dscale", "mitchell");
            }
            else
            {
                SetPropertyString("scale", "bilinear");
                SetPropertyString("cscale", "bilinear");
                SetPropertyString("dscale", "bilinear");
            }
            Console.WriteLine($"[MpvPlayer] High-quality scaling: {(enabled ? "spline36" : "bilinear")}");
        }

        /// <summary>Lead 3: use bwdif instead of the default yadif for live deinterlacing.</summary>
        public void SetSharperDeinterlacing(bool enabled)
        {
            _sharperDeinterlacing = enabled;
            ApplyDeinterlace();
            Console.WriteLine($"[MpvPlayer] Sharper deinterlacing (bwdif): {(enabled ? "on" : "off (yadif)")}");
        }

        /// <summary>Lead 2: log the FBO dimensions mpv renders into (to confirm display-native size).</summary>
        public void SetRenderLogging(bool enabled)
        {
            _logRenderDimensions = enabled;
            _lastLoggedW = -1; // force the next frame to log
            _lastLoggedH = -1;
            Console.WriteLine($"[MpvPlayer] Render dimension logging: {(enabled ? "on" : "off")}");
        }

        public void Play(string url, double? startSeconds = null)
        {
            // Set Loading before the load is queued so a fast FileLoaded can't be overwritten.
            State = PlaybackState.Loading;
            Volatile.Write(ref _timePosSeconds, 0);

            string startArg = ((int)(startSeconds ?? 0)).ToString(CultureInfo.InvariantCulture);
            Console.WriteLine($"[MpvPlayer] Playing URL: {SecretRedactor.Redact(url)} (start={startArg})");
            if (startSeconds.HasValue)
            {
                // "replace" is the default flag (replace current file)
                // "start=X" is the option
                // include playlist index of zero because we don't play playlists
                Command("loadfile", url, "replace", "0", $"start={startArg}");
            }
            else
            {
                Command("loadfile", url);
            }

            SetPropertyString("pause", "no");
        }

        public void Pause()
        {
            SetPropertyString("pause", "yes");
            State = PlaybackState.Paused;
        }

        public void Resume()
        {
            SetPropertyString("pause", "no");
            State = PlaybackState.Playing;
        }

        public void SetSubtitle(bool enabled)
        {
            // "auto" selects the best subtitle track according to "slang"
            // "no" disables subtitles
            SetPropertyString("sid", enabled ? "auto" : "no");
        }

        public void Seek(double seconds)
        {
            Command("seek", seconds.ToString("0.00", CultureInfo.InvariantCulture), "absolute");
        }

        public void SeekRelative(double seconds)
        {
            Command("seek", seconds.ToString("0.00", CultureInfo.InvariantCulture), "relative");
        }

        public void Stop()
        {
            Command("stop");
            State = PlaybackState.Idle;
        }

        /// <summary>Queues an mpv command. Returns immediately; failures are logged.</summary>
        public void Command(params string[] args)
        {
            Post(() => ExecuteCommand(args));
        }

        private void ExecuteCommand(string[] args)
        {
            var cmdString = SecretRedactor.Redact(string.Join(" ", args));
            Console.WriteLine($"[MpvPlayer] Command: {cmdString}");

            IntPtr[] pointers = new IntPtr[args.Length + 1];
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
                }
                pointers[args.Length] = IntPtr.Zero;

                int result = LibMpv.mpv_command(_mpvHandle, pointers);
                if (result < 0)
                    Console.WriteLine($"[MpvPlayer] Command failed ({ErrorString(result)}): {cmdString}");
            }
            finally
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (pointers[i] != IntPtr.Zero) Marshal.FreeCoTaskMem(pointers[i]);
                }
            }
        }

        /// <summary>Queues a property write. Returns immediately; failures are logged.</summary>
        public void SetPropertyString(string name, string value)
        {
            Post(() => SetOption(name, value));
        }

        // Synchronous write; only for the constructor and the command thread.
        private void SetOption(string name, string value)
        {
            int res = LibMpv.mpv_set_property_string(_mpvHandle, name, value);
            if (res < 0)
                Console.WriteLine($"[MpvPlayer] Setting '{name}={SecretRedactor.Redact(value)}' failed: {ErrorString(res)}");
        }

        // Synchronous read; never call from the render (UI) thread.
        private string? GetPropertyString(string name)
        {
            var ptr = LibMpv.mpv_get_property_string(_mpvHandle, name);
            if (ptr == IntPtr.Zero)
                return null;

            var value = Marshal.PtrToStringUTF8(ptr);
            LibMpv.mpv_free(ptr);
            return value;
        }

        private int GetPropertyInt(string name)
        {
            long val = 0;
            int res = LibMpv.mpv_get_property(_mpvHandle, name, LibMpv.MpvFormat.Int64, ref val);
            if (res < 0) return -1;
            return (int)val;
        }

        private static string ErrorString(int error)
        {
            var ptr = LibMpv.mpv_error_string(error);
            var text = ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
            return $"{error} ({text ?? "unknown"})";
        }

        private void LogAudioTracks()
        {
            int count = GetPropertyInt("track-list/count");
            Console.WriteLine($"[MpvPlayer] Found {count} tracks.");
            for (int i = 0; i < count; i++)
            {
                var type = GetPropertyString($"track-list/{i}/type");
                if (type == "audio")
                {
                    var id = GetPropertyString($"track-list/{i}/id");
                    var lang = GetPropertyString($"track-list/{i}/lang");
                    var title = GetPropertyString($"track-list/{i}/title");
                    var selected = GetPropertyString($"track-list/{i}/selected");
                    Console.WriteLine($"[MpvPlayer] Audio Track {i}: ID={id}, Lang={lang}, Title='{title}', Selected={selected}");
                }
            }
        }

        /// <summary>
        /// Must be called on the render thread with the GL context current (it frees the render context).
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Drain queued commands so nothing touches the handle after it is destroyed.
            _commandQueue.CompleteAdding();
            if (_commandThread != null && !_commandThread.Join(2000))
                Console.WriteLine("[MpvPlayer] Command thread did not exit within 2s");

            // Stop event loop thread; mpv_wakeup interrupts the pending mpv_wait_event.
            _eventLoopRunning = false;
            if (_mpvHandle != IntPtr.Zero)
                LibMpv.mpv_wakeup(_mpvHandle);
            if (_eventThread != null && !_eventThread.Join(2000))
                Console.WriteLine("[MpvPlayer] Event loop thread did not exit within 2s");

            if (_renderContext != IntPtr.Zero)
            {
                LibMpv.mpv_render_context_free(_renderContext);
                _renderContext = IntPtr.Zero;
            }

            if (_mpvHandle != IntPtr.Zero)
            {
                LibMpv.mpv_terminate_destroy(_mpvHandle);
                _mpvHandle = IntPtr.Zero;
            }

            if (_pFbo != IntPtr.Zero) { Marshal.FreeHGlobal(_pFbo); _pFbo = IntPtr.Zero; }
            if (_pFlipY != IntPtr.Zero) { Marshal.FreeHGlobal(_pFlipY); _pFlipY = IntPtr.Zero; }
            _renderParams = null;
            _commandQueue.Dispose();
        }
    }
}
