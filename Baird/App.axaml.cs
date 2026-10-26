using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;

namespace Baird
{
    public partial class App : Application
    {
        public IConfiguration Configuration { get; private set; }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            // Global safety nets: keep the kiosk app alive on unhandled UI/Rx exceptions.
            Dispatcher.UIThread.UnhandledException += (s, e) =>
            {
                Console.WriteLine($"[App] Unhandled UI-thread exception: {e.Exception}");
                e.Handled = true;
            };
            RxApp.DefaultExceptionHandler = new LoggingRxExceptionHandler();

            // Build Configuration
            var builder = new ConfigurationBuilder();

            // 1. Current Directory config.ini
            builder.AddIniFile(Path.Combine(Directory.GetCurrentDirectory(), "config.ini"), optional: true, reloadOnChange: true);

            // 2. User Profile .baird/config.ini
            var userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            var userConfigPath = Path.Combine(userProfile, ".baird", "config.ini");
            builder.AddIniFile(userConfigPath, optional: true, reloadOnChange: true);

            // 3. Environment Variables (override file settings)
            builder.AddEnvironmentVariables();

            Configuration = builder.Build();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // Desktop development support
                var uiMode = Baird.Services.UiModes.Read(Configuration);
                Console.WriteLine($"[App] Interface: {uiMode}");
                desktop.MainWindow = new Window
                {
                    Content = Baird.Services.UiModes.Build<Control>(uiMode, () => new Baird.NewUi.NewShellView(Configuration), () => new MainView(Configuration)),
                    Title = "Baird",
                    Background = Brushes.Black,
                };

                // Netflix plays in a browser behind Baird: the main window goes transparent where the video is. Under X11
                // a window can only be transparent if it asked for it when it was created, so ask now. Opaque until then.
                var transparentEnv = Configuration["BAIRD_TRANSPARENT_WINDOW"];
                var transparent = !bool.TryParse(transparentEnv, out var t) || t;
                if (transparent)
                {
                    desktop.MainWindow.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
                }
                var mainWindow = desktop.MainWindow;
                mainWindow.Opened += (_, _) =>
                    Console.WriteLine($"[App] Main window transparency: requested={transparent} actual={mainWindow.ActualTransparencyLevel}");

                // Add Global Focus Logging
                // Use AddClassHandler to capture all events, but filter in the lambda to reduce noise.
                InputElement.GotFocusEvent.AddClassHandler<InputElement>((sender, e) =>
                {
                    // Only log if the sender is the element that actually received focus (prevent bubbling noise)
                    if (sender != e.Source) return;

                    var source = e.Source as Control;
                    var name = source?.Name ?? "Unnamed";
                    var type = e.Source?.GetType().Name ?? "null";

                    Console.WriteLine($"[Focus Event] GotFocus: {type} ({name})");

                }, RoutingStrategies.Bubble, handledEventsToo: true);

                var fullScreenEnv = Configuration["BAIRD_FULLSCREEN"];
                if (!string.IsNullOrEmpty(fullScreenEnv) && bool.TryParse(fullScreenEnv, out bool isFullScreen) && isFullScreen)
                {
                    desktop.MainWindow.WindowState = WindowState.FullScreen;
                }
            }
            else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
            {
                // Framebuffer support
                singleViewPlatform.MainView = Baird.Services.UiModes.Read(Configuration) == Baird.Services.UiMode.New
                    ? new Baird.NewUi.NewShellView(Configuration) : new MainView(Configuration);
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>
        /// Logs exceptions that reach ReactiveUI's default handler (e.g. unhandled errors in
        /// ReactiveCommands or ObservableAsPropertyHelpers) instead of rethrowing and crashing.
        /// </summary>
        private sealed class LoggingRxExceptionHandler : IObserver<Exception>
        {
            public void OnNext(Exception value)
            {
                Console.WriteLine($"[App] Unhandled ReactiveUI exception: {value}");
            }

            public void OnError(Exception error)
            {
                Console.WriteLine($"[App] ReactiveUI exception handler error: {error}");
            }

            public void OnCompleted()
            {
            }
        }
    }
}
