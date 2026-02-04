using Avalonia;
using Avalonia.LinuxFramebuffer;
using Avalonia.Logging;
using Avalonia.ReactiveUI;
using System;
using System.Linq;
using System.Threading;
using System.Diagnostics;

namespace Baird
{
    class Program
    {

        [STAThread]
        public static void Main(string[] args)
        {
            Trace.Listeners.Add(new ConsoleTraceListener());

            var builder = BuildAvaloniaApp();
            
            if (args.Contains("--drm"))
            {
                // Silence console output, enable hardware acceleration if possible
                // Using /dev/dri/card0 by default if available
                builder.StartLinuxDrm(args, card: "/dev/dri/card0", scaling: 1.0);
            }
            else
            {
                 // Check if running on a platform where we should treat it as desktop (Windows/Linux Desktop)
                 // or force framebuffer if requested or environment detected.
                 // For this specific task, we want to enable Framebuffer on Linux when appropriate.
                 // But for development on a desktop VM, we might want standard Desktop Lifetime.
                 
                 // If we are strictly following instructions "Set up Avalonia.LinuxFramebuffer... to use /dev/dri/card0":
                 // We likely want to try to initialize that, but fallback to desktop if it fails (or if we are on X11/Wayland).
                 // However, "headless" or "framebuffer" usually implies taking over the screen.
                 
                 // Let's implement a heuristic: run as desktop unless arguments or explicit configuration say otherwise.
                 // Given the request asks to "Set up... to use /dev/dri/card0", we'll verify if we can do that conditionally.
                 // Commonly, embedded apps run with specific flags or just default to FB if no X11/Wayland.
                 
                 // Current simple approach: Default to desktop, allow FB via args, OR
                 // Since the user asked specifically for it:
                 // We will create a hybrid approach or just stick to desktop for local debug and FB for deployment.
                 // The PROMPTED configuration was: "Set up Avalonia.LinuxFramebuffer in the Program.cs to use /dev/dri/card0"
                 
                 // Let's default to standard StartWithClassicDesktopLifetime but offer a path for FB.
                 // Actually, usually you do:
                 
                 if (OperatingSystem.IsLinux() && System.Environment.GetEnvironmentVariable("DISPLAY") == null)
                 {
                      // Likely headless/embedded
                      builder.StartLinuxDrm(args, card: "/dev/dri/card0", scaling: 1.0);
                 }
                 else
                 {
                     builder.StartWithClassicDesktopLifetime(args);
                 }
            }
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(new X11PlatformOptions 
                { 
                    // force software rendering to test
                    RenderingMode = new [] { X11RenderingMode.Software } 
                })
                .LogToTrace(LogEventLevel.Verbose) // Ensure Debug level
                .WithInterFont() // Optional, but helps if system fonts are missing
                .UseReactiveUI();
    }
}
