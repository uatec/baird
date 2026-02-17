using Avalonia;
using Avalonia.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Baird.Services;
using Baird.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using Microsoft.Extensions.Configuration;

namespace Baird
{
    public partial class MainView : UserControl
    {
        private MainViewModel _viewModel;
        private List<IMediaProvider> _providers = new();
        private ICecService _cecService;
        private IHistoryService _historyService;
        private IDataService _dataService;

        // Screensaver & Idle
        private ScreensaverService? _screensaverService;
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
            var watchlistService = new JsonWatchlistService();
            var searchHistoryService = new SearchHistoryService();
            var mediaItemCache = new MediaItemCache();

            _screensaverService = new ScreensaverService();

            // Create DataService encapsulating providers and history
            _dataService = new DataService(_providers, _historyService, watchlistService, mediaItemCache);

            _viewModel = new MainViewModel(_dataService, searchHistoryService, _screensaverService, _cecService);

            DataContext = _viewModel;

            this.AttachedToVisualTree += async (s, e) =>
            {
                await _screensaverService.InitializeAsync();
                SetupIdleTimer();

                // Subscribe to VideoLayer exit requests
                var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                if (videoLayer != null)
                {
                    videoLayer.ExitRequested += OnVideoLayerExitRequested;
                }

                // TopLevel for global input hook? Or just hook on UserControl?
                // UserControl KeyDown bubbles, so focusing Root is important.
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
                if (topLevel != null)
                {
                    // Global Input Handler (Tunneling) to catch wake-up events
                    topLevel.AddHandler(InputElement.KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

                    // Existing InputCoordinator (Bubbling)
                    topLevel.KeyDown += InputCoordinator;

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

                // Restore focus to VideoPlayer when CurrentPage is cleared or showing video player
                _viewModel.ObservableForProperty(x => x.CurrentPage)
                    .Subscribe(change =>
                    {
                        if (change.Value == null || change.Value is ShowingVideoPlayerViewModel)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                                var player = videoLayer?.GetPlayer();
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
                    var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                    var player = videoLayer?.GetPlayer();
                    if (player != null)
                    {
                        Console.WriteLine("[MainView] Startup: Forcing focus to VideoPlayer");
                        player.Focus();
                    }
                }, DispatcherPriority.Input);

                // Inject DataService into VideoLayer/Player
                var vLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                if (vLayer != null)
                {
                    vLayer.DataService = _dataService;
                }

                // Subscribe to ActiveItem changes to notify VideoPlayer of current item identity
                _viewModel.ObservableForProperty(x => x.ActiveItem)
                    .Subscribe(change =>
                    {
                        var item = change.Value;
                        if (vLayer != null && item != null)
                        {
                            var mediaItem = item;
                            vLayer.GetPlayer()?.SetCurrentMediaItem(mediaItem);
                        }
                    });

                // Hook up 'Down' key from player to OpenMainMenu
                if (vLayer != null)
                {
                    var player = vLayer.GetPlayer();
                    if (player != null)
                    {
                        player.HistoryRequested += (sender, args) =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => _viewModel.OpenMainMenu());
                        };
                    }
                }


                await _viewModel.RefreshChannels();

                // Auto-play first channel
                var firstChannel = _viewModel.AllChannels.FirstOrDefault();
                if (firstChannel != null)
                {
                    Console.WriteLine($"[MainView] Auto-playing channel: {firstChannel.Name}");
                    _viewModel.PlayItem(firstChannel);
                }
                // Preload history so it's ready when user opens it
                await _viewModel.History.RefreshAsync();
                await _viewModel.Watchlist.RefreshAsync();

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
                var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
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
            if (e.Handled) return;

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

            // Q key is now handled by VideoPlayer


        }

        private void HandleBackTrigger(KeyEventArgs e)
        {
            // Always handle back - GoBack() now handles both overlay pages and video player
            _viewModel.GoBack();
            e.Handled = true;
        }

        private void OnVideoLayerExitRequested(object? sender, EventArgs e)
        {
            Console.WriteLine("[MainView] Exit requested from VideoLayer. Exiting application.");
            // Allow a small delay for any pending operations
            System.Threading.Thread.Sleep(100);
            Environment.Exit(0);
        }

        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        }
    }
}
