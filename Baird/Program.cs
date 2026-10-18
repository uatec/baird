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

            // Global safety nets: log anything that would otherwise silently kill the process.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Console.WriteLine($"[Program] AppDomain unhandled exception (IsTerminating={e.IsTerminating}): {e.ExceptionObject}");
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Console.WriteLine($"[Program] Unobserved task exception: {e.Exception}");
                e.SetObserved();
            };

            var builder = BuildAvaloniaApp();

            builder.StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .LogToTrace(LogEventLevel.Debug) // Ensure Debug level
                .WithInterFont() // Optional, but helps if system fonts are missing
                .UseReactiveUI();
    }
}
