using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Baird.Mpv
{
    public static class LibMpv
    {
        private const string MpvLibrary = "mpv";

        static LibMpv()
        {
            NativeLibrary.SetDllImportResolver(typeof(LibMpv).Assembly, DllImportResolver);
        }

        private static IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName == MpvLibrary)
            {
                string platformLibrary;
                if (OperatingSystem.IsMacOS())
                {
                    platformLibrary = "libmpv.2.dylib";
                    
                    // Try Homebrew paths first on macOS
                    string[] homebrewPaths = new[]
                    {
                        "/opt/homebrew/lib/libmpv.2.dylib",  // Apple Silicon
                        "/usr/local/lib/libmpv.2.dylib",      // Intel Mac
                        "/opt/homebrew/lib/libmpv.dylib",
                        "/usr/local/lib/libmpv.dylib"
                    };
                    
                    foreach (var path in homebrewPaths)
                    {
                        if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle))
                        {
                            return handle;
                        }
                    }
                }
                else if (OperatingSystem.IsLinux())
                {
                    platformLibrary = "libmpv.so.2";
                }
                else
                {
                    platformLibrary = "libmpv";
                }

                // Fall back to default resolution
                if (NativeLibrary.TryLoad(platformLibrary, assembly, searchPath, out IntPtr defaultHandle))
                {
                    return defaultHandle;
                }
            }

            return IntPtr.Zero;
        }

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_create();

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_initialize(IntPtr handle);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_terminate_destroy(IntPtr handle);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_command(IntPtr handle, IntPtr[] args);
        
        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_command_string(IntPtr handle, string args);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property(IntPtr handle, string name, MpvFormat format, ref int data);
        
        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property(IntPtr handle, string name, MpvFormat format, ref double data);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property_string(IntPtr handle, string name, string data);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr mpv_get_property_string(IntPtr handle, string name);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_free(IntPtr data);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_render_context_create(out IntPtr res, IntPtr mpv, MpvRenderParam[] parameters);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_render_context_free(IntPtr context);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_render_context_set_update_callback(IntPtr context, MpvRenderUpdateFn callback, IntPtr cb_ctx);

        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_render_context_render(IntPtr context, MpvRenderParam[] parameters);
        
        [DllImport(MpvLibrary, CallingConvention = CallingConvention.Cdecl)]
        public static extern void mpv_set_wakeup_callback(IntPtr handle, MpvWakeupCallback cb, IntPtr d);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void MpvRenderUpdateFn(IntPtr cb_ctx);
        
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void MpvWakeupCallback(IntPtr d);
        
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate IntPtr MpvGetProcAddressFn(IntPtr ctx, string name);

        public enum MpvFormat
        {
            None = 0,
            String = 1,
            OsGK = 2,
            Int64 = 4,
            Double = 5,
            Node = 6,
            ByteArray = 7
        }

        public enum MpvRenderParamType
        {
            Invalid = 0,
            ApiType = 1,
            InitParams = 2,
            Fbo = 3,
            FlipY = 4,
            Depth = 5,
            IccProfile = 6,
            AmbientLight = 7,
            X11Display = 8,
            WlDisplay = 9,
            AdvancedControl = 10,
            NextFrameInfo = 11,
            BlockForTargetTime = 12,
            SkipRendering = 13,
            DrmDisplay = 14,
            DrmDrawSurfaceSize = 15,
            DrmDisplayV2 = 16
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MpvRenderParam
        {
            public MpvRenderParamType Type;
            public IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MpvOpenglInitParams
        {
            public MpvGetProcAddressFn GetProcAddress;
            public IntPtr UserData;
            public IntPtr ExtraParams;
        }
        
        [StructLayout(LayoutKind.Sequential)]
        public struct MpvOpenglFbo
        {
            public int Fbo;
            public int W;
            public int H;
            public int InternalFormat;
        }
    }
}
