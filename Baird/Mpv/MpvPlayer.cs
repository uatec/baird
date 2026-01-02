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

            // Hardware acceleration configuration for Raspberry Pi
            // Try generic v4l2m2m-copy first as requested
             var hwdec = "v4l2m2m-copy"; // Fallback to "auto" if this specific one isn't available or generally works
            SetPropertyString("hwdec", hwdec);
            
            // Generic Options
            SetPropertyString("terminal", "yes");
            SetPropertyString("msg-level", "all=v");
            
            var res = LibMpv.mpv_initialize(_mpvHandle);
            if (res < 0)
                throw new Exception($"Failed to initialize mpv: {res}");
        }

        public void InitializeOpenGl(IntPtr procAddressCallback)
        {
            // Prepare Opengl params
            // We need a helper to marshal the callback
            
            // WARNING: This is a complex part. For simplicity in this iteration, 
            // we assume the usage with Avalonia's OpenGlControlBase which provides proc addresses.
            
             // Note: In a real robust implementation, we need to handle the delegte lifecycle carefully 
             // to prevent GC collection during P/Invoke.
             // For this task, we will try to keep it minimal but functional.

             // We skip detailed context creation here for now, delegating it to the Render method or initialization phase
             // where we have the GL context active. 
             // Actually, `mpv_render_context_create` needs to be called once we have a GL context. 
        }

        public void Play(string url)
        {
            Command("loadfile", url);
            SetPropertyDouble("pause", 0);
            State = PlaybackState.Playing;
        }

        public void Pause()
        {
            SetPropertyDouble("pause", 1);
            State = PlaybackState.Paused;
        }
        
        public void Resume()
        {
             SetPropertyDouble("pause", 0);
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
