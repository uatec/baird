using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Baird.Controls;
using Baird.Services;
using Baird.ViewModels;
using Microsoft.Extensions.Configuration;
using ReactiveUI;

namespace Baird;

public partial class MainView : UserControl
{
    private readonly MainViewModel _viewModel;
    private readonly List<IMediaProvider> _providers = new();
    private readonly ICecService _cecService;
    private readonly IHistoryService _historyService;
    private readonly IDataService _dataService;

    // Screensaver & Idle
    private readonly ScreensaverService? _screensaverService;
    private DispatcherTimer? _idleTimer;

    public MainView()
    {
        // Designer support
        InitializeComponent();
    }

    public MainView(IConfiguration config)
    {
        InitializeComponent();

        _providers.Add(new TvHeadendService(config));
        _providers.Add(new JellyfinService(config));
        _providers.Add(new BbcIPlayerService());
        _providers.Add(new YouTubeService());

        _cecService = new CecService();
        _historyService = new JsonHistoryService();
        var searchHistoryService = new SearchHistoryService();

        _screensaverService = new ScreensaverService();

        // Create DataService encapsulating providers and history
        _dataService = new DataService(_providers, _historyService);

        _viewModel = new MainViewModel(_dataService, searchHistoryService, _screensaverService);

        DataContext = _viewModel;

        AttachedToVisualTree += async (s, e) =>
        {
            await _screensaverService.InitializeAsync();
            SetupIdleTimer();

            // TopLevel for global input hook? Or just hook on UserControl?
            // UserControl KeyDown bubbles, so focusing Root is important.
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                // Global Input Handler (Tunneling) to catch wake-up events
                topLevel.AddHandler(InputElement.KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

                // Existing InputCoordinator (Bubbling)
                topLevel.KeyDown += InputCoordinator;
            }

            // Restore focus to VideoPlayer when CurrentPage is cleared or showing video player
            _viewModel.ObservableForProperty(x => x.CurrentPage)
                .Subscribe(change =>
                {
                    if (change.Value == null || change.Value is ShowingVideoPlayerViewModel)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            VideoLayerControl? videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                            VideoPlayer? player = videoLayer?.GetPlayer();
                            if (player != null)
                            {
                                Console.WriteLine("[MainView] CurrentPage is null or ShowingVideoPlayerViewModel, forcing focus to VideoPlayer");
                                player.Focus();
                            }
                        });
                    }
                    else
                    {
                        // When transitioning between overlay pages, we want the new page to take focus.
                        // The logic that forced focus to PageFrame has been removed as it was stealing focus 
                        // from controls (like TabNavigation) that handle their own initial focus.
                    }
                });

            // Force initial focus to VideoPlayer
            Dispatcher.UIThread.Post(() =>
            {
                VideoLayerControl? videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                VideoPlayer? player = videoLayer?.GetPlayer();
                if (player != null)
                {
                    Console.WriteLine("[MainView] Startup: Forcing focus to VideoPlayer");
                    player.Focus();
                }
            }, DispatcherPriority.Input);

            // Inject DataService into VideoLayer/Player
            VideoLayerControl? vLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
            if (vLayer != null)
            {
                vLayer.DataService = _dataService;
            }

            // Subscribe to ActiveItem changes to notify VideoPlayer of current item identity
            _viewModel.ObservableForProperty(x => x.ActiveItem)
                .Subscribe(change =>
                {
                    MediaItem? item = change.Value;
                    if (vLayer != null && item != null)
                    {
                        MediaItem mediaItem = item;
                        vLayer.GetPlayer()?.SetCurrentMediaItem(mediaItem);
                    }
                });

            // Hook up 'Down' key from player to OpenMainMenu
            if (vLayer != null)
            {
                VideoPlayer? player = vLayer.GetPlayer();
                if (player != null)
                {
                    player.HistoryRequested += (sender, args) =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => _viewModel.OpenMainMenu());
                    };
                }
            }


            await _viewModel.RefreshChannels();

            // Preload history so it's ready when user opens it
            await _viewModel.History.RefreshAsync();

            // Auto-play first channel
            MediaItem? firstChannel = _viewModel.AllChannels.FirstOrDefault();
            if (firstChannel != null)
            {
                Console.WriteLine($"[MainView] Auto-playing channel: {firstChannel.Name}");
                _viewModel.PlayItem(firstChannel);
            }

            await _cecService.StartAsync();
        };
    }

    private void SetupIdleTimer()
    {
        _idleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromHours(2)
        };
        _idleTimer.Tick += (s, e) =>
        {
            Console.WriteLine("[MainView] Idle timeout reached. Activating screensaver.");

            // Pause Main Player
            VideoLayerControl? videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
            videoLayer?.GetPlayer()?.Pause();

            _viewModel.Screensaver.Activate();
        };
        _idleTimer.Start();
    }

    private void ResetIdleTimer()
    {
        _idleTimer?.Stop();
        _idleTimer?.Start();
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // Reset timer on ANY activity
        ResetIdleTimer();

        if (_viewModel.Screensaver.IsActive)
        {
            Console.WriteLine("[MainView] Screensaver active. Waking up.");
            _viewModel.Screensaver.Deactivate();

            // Consume the event so it doesn't trigger search, pause, quit, etc.
            e.Handled = true;
        }
    }

    private void InputCoordinator(object? sender, KeyEventArgs e)
    {
        // If the event was already handled (e.g. by a focused TextBox or OnGlobalKeyDown), don't trigger global logic
        if (e.Handled)
        {
            return;
        }

        // Reset HUD Timer on any interaction
        _viewModel.ResetHudTimer();

        // Debug key press
        Console.WriteLine($"[MainView] Key: {e.Key}");

        // Back/Esc Trigger
        if (e.Key == Key.Escape || e.Key == Key.Back)
        {
            HandleBackTrigger(e);
            e.Handled = true; // Always consume Back/Esc
            return;
        }

        // Exit (Q)
        if (e.Key == Key.Q)
        {
            Console.WriteLine("[InputCoordinator] Q pressed. Exiting application.");
            // Try to save progress before exit
            VideoLayerControl? vLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
            // TODO: Move this to unload in the player itself?
            vLayer?.GetPlayer()?.SaveProgress();

            // Allow a small delay for async save? Or just hope it writes fast enough?
            System.Threading.Thread.Sleep(500);

            Environment.Exit(0);
            return;
        }

        // Power Toggle (P)
        if (e.Key == Key.P)
        {
            Console.WriteLine("[InputCoordinator] P pressed. Toggling TV Power via CEC.");
            _ = _cecService.TogglePowerAsync(); // Fire and forget
            e.Handled = true;
            return;
        }

        // DevTools Toggle (D)
        if (e.Key == Key.D)
        {
            Console.WriteLine("[InputCoordinator] D pressed. Attempting to toggle DevTools.");
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel != null)
            {
                Console.WriteLine($"[InputCoordinator] TopLevel found: {topLevel.GetType().Name}. Attaching...");
                try
                {
                    var options = new DevToolsOptions
                    {
                        Gesture = new KeyGesture(Key.F12),
                        ShowAsChildWindow = false
                    };
                    Dispatcher.UIThread.Post(() => topLevel.AttachDevTools(options));
                    Console.WriteLine("[InputCoordinator] DevTools attach command sent.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[InputCoordinator] Failed to attach DevTools: {ex}");
                    Console.WriteLine($"[MainView] StackTrace: {ex.StackTrace}");
                }
            }
            else
            {
                Console.WriteLine("[InputCoordinator] TopLevel NOT found.");
            }
            e.Handled = true;
            return;
        }
    }

    private void HandleBackTrigger(KeyEventArgs e)
    {
        // Always handle back - GoBack() now handles both overlay pages and video player
        _viewModel.GoBack();
        e.Handled = true;
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }
}
