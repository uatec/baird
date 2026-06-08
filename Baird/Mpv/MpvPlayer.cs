using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace Baird.Mpv
{
    public enum PlaybackState { Idle, Loading, Playing, Paused, Buffering }

    public class MpvPlayer : IDisposable
    {
        private IntPtr _mpvHandle;
        private IntPtr _renderContext;
        private System.Threading.Thread? _eventThread;
        private volatile bool _eventLoopRunning;
        private Action? _requestRender;

        // Video-quality feature flags (toggled live from the Settings page).
        private bool _isLive;                 // last source type, so deinterlace can be re-applied on toggle
        private bool _highQualityScaling;     // spline36 scaler vs default bilinear
        private bool _sharperDeinterlacing;   // bwdif vs default yadif
        private bool _logRenderDimensions;    // log the FBO size handed to mpv each time it changes
        private int _lastLoggedW = -1;
        private int _lastLoggedH = -1;

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

        public PlaybackState State { get; private set; } = PlaybackState.Idle;

        public bool IsMpvPaused => GetPropertyString("pause") == "yes";
        public string TimePosition => GetPropertyString("time-pos") ?? "0";
        public string Duration => GetPropertyString("duration") ?? "0";
        public string CurrentPath => GetPropertyString("path") ?? "None";
        public bool IsCoreIdle => GetPropertyString("core-idle") == "yes";
        /// <summary>
        /// Seconds of content buffered ahead of the current playback position.
        /// For live streams this approximates how far behind the live edge the viewer is.
        /// </summary>
        public string DemuxerCacheTime => GetPropertyString("demuxer-cache-time") ?? "0";

        public MpvPlayer()
        {
            _mpvHandle = LibMpv.mpv_create();
            if (_mpvHandle == IntPtr.Zero)
                throw new Exception("Failed to create mpv context");

            // Hardware acceleration: v4l2m2m-copy uses the Pi 5's rpi-hevc-dec kernel module
            // for HEVC content via /dev/video19, with frame copy-back for OpenGL compositing.
            // Falls back to software decode for codecs without a V4L2 M2M driver (e.g. H.264,
            // which has no rpi_h264_dec on this kernel). "auto-copy" tried VA-API first but
            // the Pi 5's V3D GPU has no VA-API driver, wasting time before falling back.
            Console.WriteLine("[MpvPlayer] Hardware decoding mode: v4l2m2m-copy");
            SetPropertyString("hwdec", "v4l2m2m-copy");

            // Deinterlace is configured per-source via ConfigureForSource() before each Play().
            // Default off; enabled for live broadcast streams (1080i50 UK DVB-T/T2).

            // Generics Options
            SetPropertyString("terminal", "yes");
            SetPropertyString("msg-level", "all=warn");

            // Disable default input bindings and media keys as Avalonia handles all user input
            SetPropertyString("input-default-bindings", "no");
            SetPropertyString("input-media-keys", "no");
            SetPropertyString("input-vo-keyboard", "no");

            // Critical for embedding in Avalonia/OpenGL: "libmpv" forces mpv to use the
            // render API and prevents a detached window.  For standalone (non-embedded) usage
            // on Raspberry Pi you can switch this to "drm" for direct KMS/DRM rendering,
            // but that will bypass the OpenGL render context below and won't work in this
            // embedded configuration.
            SetPropertyString("vo", "libmpv");

            // Maintain aspect ratio (will center with black bars if needed)
            SetPropertyString("keepaspect", "yes");

            // Audio filter and deinterlace are set per-source by ConfigureForSource().
            // loudnorm is omitted for live streams (1s look-ahead causes AV desync on live TV).
            SetPropertyString("softvol", "yes");
            SetPropertyString("volume-max", "200");
            SetPropertyString("volume", "100");
            // display-vdrop: drops one frame per display-cycle boundary when content and display
            // rates are slightly misaligned. Requires mpv_render_context_report_swap() after
            // each render (done in Render()) to give mpv accurate display timing feedback.
            // Better than video-sync=audio for embedded rendering because audio clock drift
            // on live streams no longer causes periodic frame-delivery misses.
            SetPropertyString("video-sync", "display-vdrop");

            // Prefer English audio
            SetPropertyString("alang", "eng,en");
            // Prefer English subtitles
            SetPropertyString("slang", "eng,en");

            var res = LibMpv.mpv_initialize(_mpvHandle);
            if (res < 0)
                throw new Exception($"Failed to initialize mpv: {res}");

            // Start event loop thread
            _eventLoopRunning = true;
            _eventThread = new System.Threading.Thread(EventLoop)
            {
                IsBackground = true,
                Name = "MpvEventLoop"
            };
            _eventThread.Start();

            // Observe properties so the event loop can raise typed C# events instead of polling
            LibMpv.mpv_observe_property(_mpvHandle, 1, "pause", LibMpv.MpvFormat.Flag);
            LibMpv.mpv_observe_property(_mpvHandle, 2, "duration", LibMpv.MpvFormat.Double);
        }

        private void EventLoop()
        {
            Console.WriteLine("[MpvPlayer] Event loop thread started");

            while (_eventLoopRunning)
            {
                try
                {
                    // Wait for events with 1 second timeout
                    IntPtr eventPtr = LibMpv.mpv_wait_event(_mpvHandle, 1.0);
                    if (eventPtr == IntPtr.Zero)
                        continue;

                    var evt = Marshal.PtrToStructure<LibMpv.MpvEvent>(eventPtr);

                    // TODO: Convert to event handlers
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
                                Console.WriteLine($"[MpvPlayer] Stream failed with error code {endFileEvent.Error}");
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
                        State = PlaybackState.Playing;
                        FileLoaded?.Invoke(this, EventArgs.Empty);
                    }
                    else if (evt.EventId == LibMpv.MpvEventId.PropertyChange)
                    {
                        if (evt.Data != IntPtr.Zero)
                        {
                            var propEvt = Marshal.PtrToStructure<LibMpv.MpvEventProperty>(evt.Data);
                            var propName = Marshal.PtrToStringUTF8(propEvt.Name) ?? "";

                            if (propName == "pause" && propEvt.Data != IntPtr.Zero)
                            {
                                bool isPaused = Marshal.ReadInt32(propEvt.Data) != 0;
                                if (isPaused)
                                    State = PlaybackState.Paused;
                                else if (State == PlaybackState.Paused)
                                    State = PlaybackState.Playing;
                                PauseStateChanged?.Invoke(this, isPaused);
                            }
                            else if (propName == "duration" && propEvt.Data != IntPtr.Zero)
                            {
                                double dur = Marshal.PtrToStructure<double>(propEvt.Data);
                                DurationChanged?.Invoke(this, dur);
                            }
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

        public void InitializeOpenGl(IntPtr procAddressCallback, Action requestRender)
        {
            _requestRender = requestRender;

            // 1. Wrap the Avalonia proc address callback
            var openglParams = new LibMpv.MpvOpenglInitParams
            {
                GetProcAddress = Marshal.GetDelegateForFunctionPointer<LibMpv.MpvGetProcAddressFn>(procAddressCallback),
                UserData = IntPtr.Zero,
                ExtraParams = IntPtr.Zero
            };

            // 2. Prepare the init params
            IntPtr openglParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(openglParams));
            Marshal.StructureToPtr(openglParams, openglParamsPtr, false);

            var renderParams = new LibMpv.MpvRenderParam[]
            {
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.ApiType, Data = Marshal.StringToHGlobalAnsi("opengl") },
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.InitParams, Data = openglParamsPtr },
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.Invalid, Data = IntPtr.Zero }
            };

            // 3. Create the context
            int res = LibMpv.mpv_render_context_create(out _renderContext, _mpvHandle, renderParams);

            // 4. Cleanup
            Marshal.FreeHGlobal(openglParamsPtr);
            Marshal.FreeHGlobal(renderParams[0].Data);

            if (res < 0) throw new Exception($"Failed to create render context: {res}");

            // 5. Set update callback
            _renderUpdateFn = UpdateCallback;
            LibMpv.mpv_render_context_set_update_callback(_renderContext, _renderUpdateFn, IntPtr.Zero);
        }

        private void UpdateCallback(IntPtr ctx)
        {
            _requestRender?.Invoke();
        }

        public void Render(int fbo, int width, int height)
        {
            if (_renderContext == IntPtr.Zero) return;

            // FBO param
            var fboParam = new LibMpv.MpvOpenglFbo { Fbo = fbo, W = width, H = height, InternalFormat = 0 };
            IntPtr pFbo = Marshal.AllocCoTaskMem(Marshal.SizeOf(fboParam));
            Marshal.StructureToPtr(fboParam, pFbo, false);

            // FlipY param
            int flipY = 1;
            IntPtr pFlipY = Marshal.AllocCoTaskMem(sizeof(int));
            Marshal.WriteInt32(pFlipY, flipY);

            // Params for render
            var paramsArr = new LibMpv.MpvRenderParam[]
            {
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.Fbo, Data = pFbo },
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.FlipY, Data = pFlipY },
                new LibMpv.MpvRenderParam { Type = LibMpv.MpvRenderParamType.Invalid, Data = IntPtr.Zero }
            };

            // Lead 2 diagnostic: confirm the FBO mpv renders into is actually display-native
            // (1920x1080). If it is smaller, Avalonia/GL upscales it afterwards = extra blur.
            if (_logRenderDimensions && (width != _lastLoggedW || height != _lastLoggedH))
            {
                _lastLoggedW = width;
                _lastLoggedH = height;
                Console.WriteLine($"[MpvPlayer] Render FBO dimensions: {width}x{height}");
            }

            LibMpv.mpv_render_context_render(_renderContext, paramsArr);
            // Report the swap so mpv gets accurate display timing feedback.
            // Required for display-sync modes (display-vdrop, display-resample) to work correctly.
            LibMpv.mpv_render_context_report_swap(_renderContext);

            Marshal.FreeCoTaskMem(pFbo);
            Marshal.FreeCoTaskMem(pFlipY);
        }

        public void UpdateVideoStatus()
        {
            if (State == PlaybackState.Loading)
            {
                // Check if we have started playing
                // Criteria: time-pos is valid (not null) OR core-idle is false
                // But time-pos might be 0 at start.
                // core-idle is usually true when loading/buffering or paused?
                // Let's use simple check: if we have a duration or time-pos

                var time = GetPropertyString("time-pos");
                // var idle = GetPropertyString("core-idle");

                if (!string.IsNullOrEmpty(time))
                {
                    State = PlaybackState.Playing;
                }
            }
        }

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
        /// Applies the deinterlacer for the current source type. Live broadcast (1080i50) must be
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

        /// <summary>Lead 1: switch between mpv's default bilinear scaler and the higher-quality
        /// spline36 (sharper upscale of the 1440x1080 anamorphic broadcast to 1920x1080).</summary>
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
            Console.WriteLine($"[MpvPlayer] High-quality scaling: {(enabled ? "spline36" : "bilinear (default)")}");
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
            int startSecondsInt = (int)(startSeconds ?? 0);
            Console.WriteLine($"[MpvPlayer] Playing URL: {url} (start={startSecondsInt.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
            if (startSeconds.HasValue)
            {
                // "replace" is the default flag (replace current file)
                // "start=X" is the option
                // include playlist index of zero because we don't play playlists
                Command("loadfile", url, "replace", "0", $"start={startSecondsInt.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            }
            else
            {
                Command("loadfile", url);
            }

            SetPropertyString("pause", "no");
            State = PlaybackState.Loading;
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
            Command("seek", seconds.ToString("0.00"), "absolute");
        }

        public void Stop()
        {
            Command("stop");
            State = PlaybackState.Idle;
        }

        public void Command(params string[] args)
        {
            // Marshaling string array to IntPtr[] is required for mpv_command
            // But mpv_command_string is easier for simple commands
            var cmdString = string.Join(" ", args); // Simple join might generally work for simple args, but quoting is safer.
                                                    // However, LibMpv.mpv_command expects null-terminated array.

            Console.WriteLine($"[MpvPlayer] Command: {cmdString}");
            IntPtr[] pointers = new IntPtr[args.Length + 1];
            for (int i = 0; i < args.Length; i++)
            {
                pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            }
            pointers[args.Length] = IntPtr.Zero;

            int result = LibMpv.mpv_command(_mpvHandle, pointers);

            for (int i = 0; i < args.Length; i++)
            {
                Marshal.FreeCoTaskMem(pointers[i]);
            }

            if (result < 0)
                Console.WriteLine($"[MpvPlayer] Command failed (error {result}): {cmdString}");
        }

        public void SetPropertyString(string name, string value)
        {
            LibMpv.mpv_set_property_string(_mpvHandle, name, value);
        }

        public void SetPropertyDouble(string name, double value)
        {
            int res = LibMpv.mpv_set_property(_mpvHandle, name, LibMpv.MpvFormat.Double, ref value);
        }

        public string? GetPropertyString(string name)
        {
            var ptr = LibMpv.mpv_get_property_string(_mpvHandle, name);
            if (ptr == IntPtr.Zero)
                return null;

            var value = Marshal.PtrToStringUTF8(ptr);
            LibMpv.mpv_free(ptr);
            return value;
        }

        public int GetPropertyInt(string name)
        {
            long val = 0;
            int res = LibMpv.mpv_get_property(_mpvHandle, name, LibMpv.MpvFormat.Int64, ref val);
            if (res < 0) return -1;
            return (int)val;
        }


        public int GetTrackCount()
        {
            return GetPropertyInt("track-list/count");
        }

        public void LogAudioTracks()
        {
            int count = GetTrackCount();
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

        public IntPtr Handle => _mpvHandle;

        public void Dispose()
        {
            // Stop event loop thread first
            _eventLoopRunning = false;
            if (_eventThread != null && _eventThread.IsAlive)
            {
                // Give it a moment to exit gracefully
                _eventThread.Join(2000);
            }

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
        }
    }
}
