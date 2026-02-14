using System.Diagnostics;
using Avalonia;
using Avalonia.Logging;
using Avalonia.ReactiveUI;

namespace Baird;

internal class Program
{

    [STAThread]
    public static void Main(string[] args)
    {
        Trace.Listeners.Add(new ConsoleTraceListener());

        AppBuilder builder = BuildAvaloniaApp();

        builder.StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace(LogEventLevel.Debug) // Ensure Debug level
            .WithInterFont() // Optional, but helps if system fonts are missing
            .UseReactiveUI();
}
