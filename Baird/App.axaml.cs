using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;

namespace Baird;

public partial class App : Application
{
    public IConfiguration Configuration { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Build Configuration
        var builder = new ConfigurationBuilder();

        // 1. Current Directory config.ini
        builder.AddIniFile(Path.Combine(Directory.GetCurrentDirectory(), "config.ini"), optional: true, reloadOnChange: true);

        // 2. User Profile .baird/config.ini
        string userProfile = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        string userConfigPath = Path.Combine(userProfile, ".baird", "config.ini");
        builder.AddIniFile(userConfigPath, optional: true, reloadOnChange: true);

        // 3. Environment Variables (override file settings)
        builder.AddEnvironmentVariables();

        Configuration = builder.Build();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Desktop development support
            desktop.MainWindow = new Window
            {
                Content = new MainView(Configuration),
                Title = "Baird"
            };

            string? fullScreenEnv = Configuration["BAIRD_FULLSCREEN"];
            if (!string.IsNullOrEmpty(fullScreenEnv) && bool.TryParse(fullScreenEnv, out bool isFullScreen) && isFullScreen)
            {
                desktop.MainWindow.WindowState = WindowState.FullScreen;
            }
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            // Framebuffer support
            singleViewPlatform.MainView = new MainView(Configuration);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
