using System;
using System.Runtime.InteropServices;
using Avalonia.OpenGL;

namespace Baird.Mpv
{
    public enum PlaybackState { Idle, Playing, Paused, Buffering }

    public class MpvPlayer : IDisposable
    {
        private IntPtr _mpvHandle;
        private IntPtr _renderContext;
        
        public PlaybackState State { get; private set; } = PlaybackState.Idle;

        public bool IsMpvPaused => GetPropertyString("pause") == "yes";
        public string TimePosition => GetPropertyString("time-pos") ?? "0";
        public string Duration => GetPropertyString("duration") ?? "0";
        public string CurrentPath => GetPropertyString("path") ?? "None";
        
        public MpvPlayer()
        {
            _mpvHandle = LibMpv.mpv_create();
            if (_mpvHandle == IntPtr.Zero)
                throw new Exception("Failed to create mpv context");

            // Hardware acceleration configuration
            // Use "auto" to let mpv pick the best available (videotoolbox on macOS, vaapi/nvdec/v4l2m2m on Linux)
             var hwdec = "auto"; 
            SetPropertyString("hwdec", hwdec);

            // Generic Options
            SetPropertyString("terminal", "yes");
            SetPropertyString("msg-level", "all=v");
            
            // Critical for embedding: Force libmpv VO to prevent detached window
            SetPropertyString("vo", "libmpv");

            // Maintain aspect ratio (will center with black bars if needed)
            SetPropertyString("keepaspect", "yes");

            var res = LibMpv.mpv_initialize(_mpvHandle);
            if (res < 0)
                throw new Exception($"Failed to initialize mpv: {res}");
        }

        // Add this field to your class
        // private IntPtr _renderContext;

        public void InitializeOpenGl(IntPtr procAddressCallback)
        {
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

            // 3. Create the context (The missing link!)
            int res = LibMpv.mpv_render_context_create(out _renderContext, _mpvHandle, renderParams);

            // 4. Cleanup
            Marshal.FreeHGlobal(openglParamsPtr);
            Marshal.FreeHGlobal(renderParams[0].Data);

            if (res < 0) throw new Exception($"Failed to create render context: {res}");
        }

        public void Play(string url)
        {
            Command("loadfile", url);
            SetPropertyString("pause", "no");
            State = PlaybackState.Playing;
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

        public void Seek(double seconds)
        {
            Command("seek", seconds.ToString("0.00"), "absolute");
        }

        public void Stop()
        {
            Command("stop");
            State = PlaybackState.Idle;
        }

        private void Command(params string[] args)
        {
             // Marshaling string array to IntPtr[] is required for mpv_command
             // But mpv_command_string is easier for simple commands
             var cmdString = string.Join(" ", args); // Simple join might generally work for simple args, but quoting is safer.
             // However, LibMpv.mpv_command expects null-terminated array.
             
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
        
        public string GetPropertyString(string name)
        {
            var ptr = LibMpv.mpv_get_property_string(_mpvHandle, name);
            if (ptr == IntPtr.Zero)
                return null;
                
            var value = Marshal.PtrToStringUTF8(ptr);
            LibMpv.mpv_free(ptr);
            return value;
        }
        
        public IntPtr Handle => _mpvHandle;

        public void Dispose()
        {
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
