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

        private LibMpv.MpvRenderUpdateFn? _renderUpdateFn;
        public event EventHandler? StreamEnded;

        public PlaybackState State { get; private set; } = PlaybackState.Idle;

        public bool IsMpvPaused => GetPropertyString("pause") == "yes";
        public string TimePosition => GetPropertyString("time-pos") ?? "0";
        public string Duration => GetPropertyString("duration") ?? "0";
        public string CurrentPath => GetPropertyString("path") ?? "None";
        public bool IsCoreIdle => GetPropertyString("core-idle") == "yes";

        public MpvPlayer()
        {
            _mpvHandle = LibMpv.mpv_create();
            if (_mpvHandle == IntPtr.Zero)
                throw new Exception("Failed to create mpv context");

            // Hardware acceleration configuration
            // RPi 5: "auto-copy" ensures decoded frames are copied back to system memory
            // which is often necessary when embedding mpv in Avalonia/OpenGL to avoid
            // DRM/KMS overlay issues that might bypass the UI.
            // "yes" for deinterlace is critical for 1080i50 broadcasts (UK Satellite/Terrestrial).
            var hwdec = "no";
            SetPropertyString("hwdec", hwdec);
            SetPropertyString("deinterlace", "yes");

            // Generics Options
            SetPropertyString("terminal", "yes");
            SetPropertyString("msg-level", "all=warn");

            // Critical for embedding: Force libmpv VO to prevent detached window
            SetPropertyString("vo", "libmpv");

            // Maintain aspect ratio (will center with black bars if needed)
            SetPropertyString("keepaspect", "yes");

            // --- Synchronization & Anti-Tearing ---
            // DISABLED for RPi 5 Performance. Interpolation is very heavy on GPU.
            // SetPropertyString("video-sync", "display-resample");
            // SetPropertyString("interpolation", "yes");
            // SetPropertyString("tscale", "oversample"); 
            SetPropertyString("opengl-swapinterval", "1"); // VSync

            // Standard sync
            SetPropertyString("video-sync", "audio");

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

                            // Only fire StreamEnded for natural EOF, not for stop/error/quit
                            if (endFileEvent.Reason == LibMpv.MpvEndFileReason.Eof)
                            {
                                Console.WriteLine("[MpvPlayer] Stream ended naturally (EOF)");
                                StreamEnded?.Invoke(this, EventArgs.Empty);
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

            LibMpv.mpv_render_context_render(_renderContext, paramsArr);

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

            LibMpv.mpv_command(_mpvHandle, pointers);

            for (int i = 0; i < args.Length; i++)
            {
                Marshal.FreeCoTaskMem(pointers[i]);
            }
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
