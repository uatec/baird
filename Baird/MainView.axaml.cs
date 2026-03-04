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
        private IEpgService _epgService = null!;
        private ICecService _cecService;
        private IHistoryService _historyService;
        private IDataService _dataService;
        private IJellyseerrService _jellyseerrService;

        // Screensaver & Idle
        private ScreensaverService? _screensaverService;
        private DispatcherTimer? _idleTimer;
        private bool _wasPausedForScreensaver = false; // Track if we actively paused for screensaver
        private bool _pausedForCecStandby = false; // Track if we auto-paused because TV went to standby
        private bool _inputsBlocked = false; // Inputs are blocked when TV is off or on a different input
        private DispatcherTimer? _inputUnblockFallbackTimer; // Fallback unblock when TV switches silently (no Request Active Source)
        private DateTime _lastCecAssert = DateTime.MinValue;
        private static readonly TimeSpan CecAssertCooldown = TimeSpan.FromSeconds(30);

        public MainView()
        {
            // Designer support
            InitializeComponent();
        }

        public MainView(IConfiguration config)
        {
            InitializeComponent();

            var tvh = new TvHeadendService(config);
            _providers.Add(tvh);
            _epgService = tvh;
            _providers.Add(new JellyfinService(config));
            _providers.Add(new BbcIPlayerService());
            _providers.Add(new YouTubeService());

            _cecService = new CecService();
            _historyService = new JsonHistoryService();
            var watchlistService = new JsonWatchlistService();
            var searchHistoryService = new SearchHistoryService();
            var mediaItemCache = new MediaItemCache();
            var mediaDataCache = new JsonMediaDataCache();

            _screensaverService = new ScreensaverService();
            _jellyseerrService = new JellyseerrService(config);

            // Create DataService encapsulating providers and history
            _dataService = new DataService(_providers, _historyService, watchlistService, mediaItemCache, mediaDataCache);

            _viewModel = new MainViewModel(config, _dataService, searchHistoryService, _screensaverService, _cecService, _jellyseerrService, _epgService);

            DataContext = _viewModel;

            this.AttachedToVisualTree += async (s, e) =>
            {
                Console.WriteLine("[MainView] Attached to visual tree. Starting initialization...");

                Console.WriteLine("[MainView] Initializing screensaver service...");
                // Fire-and-forget: screensaver data only needed after 30min idle timeout
                _ = Task.Run(() => _screensaverService.InitializeAsync());
                SetupIdleTimer();

                // Yield to let dispatcher process any queued callbacks (e.g. from fire-and-forget tasks)
                await Task.Yield();

                // Subscribe to VideoLayer exit requests
                var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                if (videoLayer != null)
                {
                    videoLayer.ExitRequested += OnVideoLayerExitRequested;
                }

                Console.WriteLine("[MainView] Setting up input handling...");

                // TopLevel for global input hook? Or just hook on UserControl?
                // UserControl KeyDown bubbles, so focusing Root is important.
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);


                if (topLevel != null)
                {
                    // Global Input Handler (Tunneling) to catch wake-up events
                    topLevel.AddHandler(InputElement.KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
                    topLevel.AddHandler(InputElement.KeyUpEvent, OnGlobalKeyUp, RoutingStrategies.Tunnel);
                    topLevel.AddHandler(InputElement.PointerPressedEvent, OnGlobalPointerActivity, RoutingStrategies.Tunnel);
                    topLevel.AddHandler(InputElement.PointerMovedEvent, OnGlobalPointerActivity, RoutingStrategies.Tunnel);

                    // Existing InputCoordinator (Bubbling)
                    topLevel.KeyDown += InputCoordinator;

                    try
                    {
                        // var options = new DevToolsOptions
                        // {
                        //     Gesture = new KeyGesture(Key.F12),
                        //     ShowAsChildWindow = false
                        // };
                        // Dispatcher.UIThread.Post(() => topLevel.AttachDevTools(options));
                        // Console.WriteLine("[InputCoordinator] DevTools attach command sent.");
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

                // Yield to let dispatcher process queued callbacks before heavy I/O
                await Task.Yield();

                Console.WriteLine("[MainView] Refreshing channels...");
                try
                {
                    await _viewModel.RefreshChannels();
                    Console.WriteLine("[MainView] Channels refreshed.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainView] Failed to refresh channels: {ex.Message}");
                }

                // Auto-play first channel
                var firstChannel = _viewModel.AllChannels.FirstOrDefault();
                if (firstChannel != null)
                {
                    Console.WriteLine($"[MainView] Auto-playing channel: {firstChannel.Name}");
                    _viewModel.PlayItem(firstChannel);
                }

                Console.WriteLine("[MainView] Preloading history and watchlist...");
                // Preload history so it's ready when user opens it
                await _viewModel.History.RefreshAsync();
                await _viewModel.Watchlist.RefreshAsync();

                Console.WriteLine("[MainView] Starting CEC service...");

                // Wire CEC TV power events for auto-pause/resume
                _cecService.TvStandby += (s, e) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Console.WriteLine("[MainView] TV standby via CEC — blocking inputs.");
                        _inputsBlocked = true;

                        var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                        var player = videoLayer?.GetPlayer();
                        if (player != null && player.GetState() == Baird.Mpv.PlaybackState.Playing)
                        {
                            Console.WriteLine("[MainView] TV standby via CEC — pausing video.");
                            player.Pause();
                            _pausedForCecStandby = true;
                        }
                    });
                };

                _cecService.TvPowerOn += (s, e) =>
                {
                    // TV is awake — claim the input. Unblocking happens via InputRegained
                    // once cec-client confirms our Active Source assertion went out.
                    Console.WriteLine("[MainView] TV power on via CEC — asserting active source.");
                    _ = _cecService.ChangeInputToThisDeviceAsync();
                };

                _cecService.InputLost += (s, e) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Console.WriteLine("[MainView] Input lost via CEC — blocking inputs.");
                        _inputsBlocked = true;
                    });
                };

                _cecService.InputRegained += (s, e) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Console.WriteLine("[MainView] Input regained via CEC — unblocking inputs.");
                        UnblockInputs();
                    });
                };

                // Start CEC Service in background so it doesn't block UI startup if it fails/hangs
                _ = _cecService.StartAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        Console.WriteLine($"[MainView] CEC Service failed to start: {t.Exception?.InnerException?.Message}");
                        return;
                    }

                    if (_cecService.IsAvailable)
                    {
                        // CEC is running — we don't know the TV's power state or whether we
                        // have the active input (e.g. app just restarted after a software update
                        // while the TV was off). Start with inputs blocked and assert active source.
                        // The TV will send Request Active Source when it's ready, which unblocks us.
                        Dispatcher.UIThread.Post(() =>
                        {
                            Console.WriteLine("[MainView] CEC available — blocking inputs until active source confirmed.");
                            _inputsBlocked = true;
                            _ = _cecService.ChangeInputToThisDeviceAsync();
                        });
                    }
                });
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
                if (_viewModel.Screensaver.IsActive) return;

                Console.WriteLine("[MainView] Idle timeout reached. Activating screensaver.");

                var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                var player = videoLayer?.GetPlayer();

                if (player != null && player.GetState() == Baird.Mpv.PlaybackState.Playing)
                {
                    Console.WriteLine("[MainView] Pausing active video for screensaver.");
                    player.Pause();
                    _wasPausedForScreensaver = true;
                }
                else
                {
                    _wasPausedForScreensaver = false;
                }

                _viewModel.Screensaver.Activate();
            };
            _idleTimer.Start();
        }

        private void ResetIdleTimer()
        {
            _idleTimer?.Stop();
            _idleTimer?.Start();
        }

        /// <summary>
        /// On any user interaction, wake the TV and claim our AV input — but no more than once per
        /// <see cref="CecAssertCooldown"/> to avoid flooding the CEC bus.
        /// </summary>
        private void AssertCecPresence()
        {
            if (DateTime.Now - _lastCecAssert < CecAssertCooldown) return;
            _lastCecAssert = DateTime.Now;
            Console.WriteLine("[MainView] User activity — asserting CEC presence (wake TV + active source).");
            _ = _cecService.PowerOnAsync();
            _ = _cecService.ChangeInputToThisDeviceAsync();
        }

        /// <summary>
        /// Sends CEC commands to reclaim the TV's active input and starts a fallback timer.
        /// The fallback unblocks inputs after 6 s if the TV never sends Request Active Source
        /// (e.g. the TV was already on and switched silently without broadcasting that event).
        /// </summary>
        private void RequestInputRegain()
        {
            Console.WriteLine("[MainView] Requesting input regain — powering on TV + asserting active source.");
            _ = _cecService.PowerOnAsync();
            _ = _cecService.ChangeInputToThisDeviceAsync();

            // Cancel any existing fallback so repeated presses don't stack timers
            _inputUnblockFallbackTimer?.Stop();
            _inputUnblockFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _inputUnblockFallbackTimer.Tick += (s, e) =>
            {
                _inputUnblockFallbackTimer?.Stop();
                if (_inputsBlocked)
                {
                    Console.WriteLine("[MainView] Fallback timer fired — unblocking inputs without CEC confirmation.");
                    UnblockInputs();
                }
            };
            _inputUnblockFallbackTimer.Start();
        }

        /// <summary>
        /// Unblocks inputs and restores any paused video or active screensaver.
        /// Called either from the CEC InputRegained event or the fallback timer.
        /// </summary>
        private void UnblockInputs()
        {
            _inputUnblockFallbackTimer?.Stop();
            _inputsBlocked = false;

            if (_viewModel.Screensaver.IsActive)
                _viewModel.Screensaver.Deactivate();

            var videoLayer = this.FindControl<Controls.VideoLayerControl>("VideoLayer");
            var player = videoLayer?.GetPlayer();

            if (_pausedForCecStandby)
            {
                Console.WriteLine("[MainView] Resuming video after CEC standby.");
                player?.Resume();
                _pausedForCecStandby = false;
            }

            if (_wasPausedForScreensaver)
            {
                Console.WriteLine("[MainView] Resuming video after screensaver (CEC-triggered wake).");
                player?.Resume();
                _wasPausedForScreensaver = false;
            }
        }

        private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
        {
            // While inputs are blocked (TV off or wrong input), any key press requests focus back
            // but does NOT take effect — we wait for CEC to confirm we have the active source
            // (or for the fallback timer to fire if the TV switches silently).
            if (_inputsBlocked)
            {
                Console.WriteLine($"[MainView] Inputs blocked. Key '{e.Key}' requesting TV power + active source.");
                RequestInputRegain();
                e.Handled = true;
                return;
            }

            // Reset timer on ANY activity
            ResetIdleTimer();
            AssertCecPresence();

            // Wake up if screensaver is active
            if (_viewModel.Screensaver.IsActive)
            {
                Console.WriteLine($"[MainView] Screensaver active. Key '{e.Key}' pressed. Waking up.");
                _viewModel.Screensaver.Deactivate();

                // Restore playback if we paused it for screensaver
                if (_wasPausedForScreensaver)
                {
                    Console.WriteLine("[MainView] Resuming video playback after screensaver.");
                    Dispatcher.UIThread.Post(() =>
                    {
                        var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                        var player = videoLayer?.GetPlayer();
                        if (player != null)
                        {
                            player.Resume();
                        }
                    });
                    _wasPausedForScreensaver = false;
                }

                // Consume the event so it doesn't trigger search, pause, quit, etc.
                e.Handled = true;
                return;
            }

            // Remap TV remote OK (KEY_SELECT) → Enter before any control sees it.
            // KeyEventArgs.Key is init-only, so we consume the original and re-raise as Enter.
            if (e.Key == Key.Select)
            {
                e.Handled = true;
                (e.Source as Interactive)?.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyDownEvent,
                    Key = Key.Enter,
                    KeyModifiers = e.KeyModifiers,
                    Source = e.Source,
                });
            }
        }

        private void OnGlobalKeyUp(object? sender, KeyEventArgs e)
        {
            // Mirror the KeyDown remap so MediaButton's long-press KeyUp handling sees Key.Enter.
            if (e.Key == Key.Select)
            {
                e.Handled = true;
                (e.Source as Interactive)?.RaiseEvent(new KeyEventArgs
                {
                    RoutedEvent = InputElement.KeyUpEvent,
                    Key = Key.Enter,
                    KeyModifiers = e.KeyModifiers,
                    Source = e.Source,
                });
            }
        }

        private void OnGlobalPointerActivity(object? sender, PointerEventArgs e)
        {
            if (_inputsBlocked)
            {
                Console.WriteLine("[MainView] Inputs blocked. Pointer activity requesting TV power + active source.");
                RequestInputRegain();
                e.Handled = true;
                return;
            }

            ResetIdleTimer(); // Activity resets timer
            AssertCecPresence();

            if (_viewModel.Screensaver.IsActive)
            {
                Console.WriteLine("[MainView] Screensaver active. Pointer activity. Waking up.");
                _viewModel.Screensaver.Deactivate();

                // Restore playback if we paused it for screensaver
                if (_wasPausedForScreensaver)
                {
                    Console.WriteLine("[MainView] Resuming video playback after screensaver (pointer).");
                    Dispatcher.UIThread.Post(() =>
                    {
                        var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                        var player = videoLayer?.GetPlayer();
                        if (player != null)
                        {
                            player.Resume();
                        }
                    });
                    _wasPausedForScreensaver = false;
                }
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
            if (e.Key == Key.Escape || e.Key == Key.BrowserBack)
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
