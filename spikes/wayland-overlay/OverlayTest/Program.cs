using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace OverlayTest;

/// <summary>
/// Stand-in for Baird's UI layer in the Wayland overlay spike.
///
/// A fullscreen, undecorated, transparent window: a semi-transparent HUD bar along the bottom
/// (like Baird's video HUD), an animated marker so you can see the UI repainting over video, and
/// an opaque "menu" panel that can be toggled (O key, or automatically with --cycle) to simulate
/// opening Baird's menus. Logs what the platform actually gave us so the run script can grade it.
///
/// Keys: O toggle opaque panel, H toggle HUD, Q/Escape quit.
/// </summary>
public static class Program
{
    public const string WmClass = "baird-overlay";

    [STAThread]
    public static int Main(string[] args) =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // WM_CLASS is how labwc's window rules identify this (XWayland) window.
            .With(new X11PlatformOptions { WmClass = WmClass })
            .WithInterFont()
            .StartWithClassicDesktopLifetime(args);
}

public class App : Application
{
    public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();
            desktop.MainWindow = new OverlayWindow(cycle: args.Contains("--cycle"));
        }
        base.OnFrameworkInitializationCompleted();
    }
}

public class OverlayWindow : Window
{
    private static readonly TimeSpan CyclePeriod = TimeSpan.FromSeconds(10);

    private readonly Border _hud;
    private readonly Border _menu;
    private readonly Border _marker;
    private readonly TextBlock _status;
    private readonly DateTime _started = DateTime.Now;
    private int _ticks;

    public OverlayWindow(bool cycle)
    {
        Title = "Baird overlay spike";
        SystemDecorations = SystemDecorations.None;
        WindowState = WindowState.FullScreen;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        _status = new TextBlock { Foreground = Brushes.White, FontSize = 28, VerticalAlignment = VerticalAlignment.Center };
        _marker = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Brushes.OrangeRed };

        _hud = new Border
        {
            // Same idea as Baird's HUD: translucent black, video visible through it.
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            Height = 140,
            VerticalAlignment = VerticalAlignment.Bottom,
            Padding = new Thickness(40, 20),
            Child = new StackPanel
            {
                Spacing = 12,
                Children = { _status, new Canvas { Height = 24, Children = { _marker } } },
            },
        };

        _menu = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
            Width = 900,
            Height = 500,
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = new TextBlock
            {
                Text = "Opaque menu panel\n(video must be hidden behind this)",
                Foreground = Brushes.White,
                FontSize = 36,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        Content = new Grid { Children = { _menu, _hud } };

        // ~50 Hz animation so UI repaints are continuous, like a ticking HUD over video.
        var anim = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Render, (_, _) => Animate());
        anim.Start();

        if (cycle)
        {
            var cycler = new DispatcherTimer(CyclePeriod, DispatcherPriority.Normal, (_, _) => ToggleMenu("cycle"));
            cycler.Start();
        }

        Opened += (_, _) => Dispatcher.UIThread.Post(Report, DispatcherPriority.Background);
        Activated += (_, _) => Log("focus: activated (overlay has keyboard focus)");
        Deactivated += (_, _) => Log("focus: DEACTIVATED (another window took focus)");
    }

    private void Report()
    {
        Log($"actual-transparency={ActualTransparencyLevel}");
        Log($"window-state={WindowState} topmost={Topmost} client-size={ClientSize.Width}x{ClientSize.Height} scaling={RenderScaling}");
        Log($"platform={(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { } w ? $"wayland-env={w}" : "no-wayland-env")} display={Environment.GetEnvironmentVariable("DISPLAY")}");
        Log("ready");
    }

    private void Animate()
    {
        _ticks++;
        double width = Math.Max(1, ClientSize.Width - 80 - 24);
        double phase = (_ticks % 200) / 200.0;
        Canvas.SetLeft(_marker, width * (phase < 0.5 ? phase * 2 : (1 - phase) * 2));
        if (_ticks % 10 == 0)
        {
            var up = DateTime.Now - _started;
            _status.Text = $"{DateTime.Now:HH:mm:ss}  overlay up {up:mm\\:ss}  transparency={ActualTransparencyLevel}  menu={(_menu.IsVisible ? "shown" : "hidden")}";
        }
    }

    private void ToggleMenu(string why)
    {
        _menu.IsVisible = !_menu.IsVisible;
        Log($"menu {(_menu.IsVisible ? "shown" : "hidden")} ({why})");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        Log($"key {e.Key}");
        switch (e.Key)
        {
            case Key.O: ToggleMenu("key"); break;
            case Key.H: _hud.IsVisible = !_hud.IsVisible; break;
            case Key.Q:
            case Key.Escape: Close(); break;
        }
    }

    private static void Log(string message) =>
        Console.WriteLine($"[overlay] {DateTime.Now:HH:mm:ss.fff} {message}");
}
