using Avalonia;
using Avalonia.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Baird.Services;
using Baird.ViewModels;
using System;
using System.Linq;
using System.Reactive.Disposables;
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
        private ISpeechToTextService _speechService = null!;
        private bool _isListeningForVoice = false;
        private Baird.ViewModels.ISearchTextInput? _voiceTarget;

        // Visual-tree subscription lifetime tracking
        private readonly CompositeDisposable _visualTreeSubscriptions = new();

        // Screensaver & Idle
        private ScreensaverService? _screensaverService;
        private DispatcherTimer? _idleTimer;
        private bool _wasPausedForScreensaver = false; // Track if we actively paused for screensaver
        private bool _pausedForCecStandby = false; // Track if we auto-paused because TV went to standby
        private volatile bool _inputsBlocked = false; // Inputs are blocked when TV is off or on a different input
        private bool _inputLockDisabled; // Input locking disabled by default; set BAIRD_ENABLE_INPUT_LOCK=true to block inputs when TV switches source
        private volatile bool _weAreIntendedActiveSource = false; // True when Baird should be the TV's active source; false when another device (e.g. Chromecast) is in use
        private DispatcherTimer? _inputUnblockFallbackTimer; // Fallback unblock when TV switches silently (no Request Active Source)
        private DateTime _lastCecAssert = DateTime.MinValue;
        private static readonly TimeSpan CecAssertCooldown = TimeSpan.FromSeconds(30);
        private bool _tvPowerToggleInFlight;
        private DateTime _lastTvPowerToggle = DateTime.MinValue;
        private static readonly TimeSpan TvPowerToggleCooldown = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan VolumeRepeatInterval = TimeSpan.FromMilliseconds(300);
        private readonly RemoteKeyGate _volumeUpGate = new(VolumeRepeatInterval);
        private readonly RemoteKeyGate _volumeDownGate = new(VolumeRepeatInterval);
        private readonly RemoteKeyGate _muteGate = new(TimeSpan.MaxValue);

        // Voice command key — loaded from config (VOICE_COMMAND_KEY).
        // Avalonia's Key enum integers do NOT correspond to evdev keycodes; they go through
        // XKB → keysym → Avalonia Key. Because KEY_VOICECOMMAND (evdev 0x246) is unmapped in
        // most XKB tables it may arrive as Key.None or an unexpected value.
        // To discover the real value: set BAIRD_LOG_KEYS=true in config.ini and watch the console
        // while pressing the voice button, then set VOICE_COMMAND_KEY to the logged integer.
        private Key _voiceCommandKey = Key.None;

        private readonly bool _mouseEnabled;

        public MainView()
        {
            // Designer support
            InitializeComponent();
        }

        public MainView(IConfiguration config)
        {
            InitializeComponent();

            // The mouse is off by default (Baird is driven by the remote); BAIRD_MOUSE=true brings it back for desktop development.
            _mouseEnabled = bool.TryParse(config["BAIRD_MOUSE"], out var mouse) && mouse;

            // Hover states come from hit-testing, which runs before any handler can swallow the event, so a pointer that is
            // hidden but still moving would flicker the buttons it passes over. Nothing under the window is hit-testable;
            // the window itself still gets the clicks (see OnGlobalPointerPressed).
            if (!_mouseEnabled) IsHitTestVisible = false;

            var tvh = new TvHeadendService(config);
            _providers.Add(tvh);
            _epgService = tvh;
            _providers.Add(new JellyfinService(config));
            _providers.Add(new BbcIPlayerService());
            _providers.Add(new YouTubeService());

            // One shared browser for Netflix search and playback (they share the signed-in profile).
            var netflixCredentials = NetflixCredentials.FromConfiguration(config);
            var netflixBrowser = new CdpBrowserService();
            netflixBrowser.LogMessage += m => Console.WriteLine($"[NetflixBrowser] {m}");
            var netflixSession = new NetflixSession(netflixBrowser, netflixCredentials,
                config["NETFLIX_PROFILE"] is { Length: > 0 } nfProfile ? nfProfile : null);
            netflixSession.Log += m => Console.WriteLine($"[NetflixSession] {m}");
            if (netflixCredentials != null)
            {
                _providers.Add(new NetflixProvider(netflixSession));
                // Open, sign in and pick the profile in the background, so the first search or play does not wait for it.
                netflixSession.Start(TimeSpan.FromSeconds(10));
            }
            else Console.WriteLine("[MainView] Netflix not in search: no NETFLIX_USER/NETFLIX_PASS in config.ini.");

            // Disney+ uses the same browser, window and tab (netflixSession.Hub): one service at a time, each navigates the tab to
            // its own site when it takes over. It signs in on first use; it does not open the browser at start-up.
            var disneyCredentials = DisneyCredentials.FromConfiguration(config);
            var disneyService = new DisneyService(netflixSession.Hub, disneyCredentials,
                config["DISNEY_PROFILE"] is { Length: > 0 } dnProfile ? dnProfile : null);
            disneyService.Log += m => Console.WriteLine($"[DisneyService] {m}");
            if (disneyCredentials != null)
            {
                _providers.Add(new DisneyProvider(disneyService));
                // Open (or attach to) the browser, sign in and take the API token in the background, after Netflix's own start-up,
                // so the first search or play does not wait for it.
                disneyService.Start(TimeSpan.FromSeconds(30));
            }
            else Console.WriteLine("[MainView] Disney+ not in search: no DISNEY_USER/DISNEY_PASS in config.ini.");

            // Channel 4's search and episode lists are public (plain HTTP), so it is always in search. Playing needs the shared browser
            // signed in, by the viewer (Channel 4 refuses automated sign-ins): the player offers "Press OK to sign in" when it is not.
            var channel4Service = new Channel4Service(netflixSession.Hub, new Channel4Catalogue());
            channel4Service.Log += m => Console.WriteLine($"[Channel4Service] {m}");
            _providers.Add(new Channel4Provider(channel4Service.Catalogue));

            _cecService = new CecService();
            _historyService = new JsonHistoryService();
            var watchlistService = new JsonWatchlistService();
            var searchHistoryService = new SearchHistoryService();
            var mediaItemCache = new MediaItemCache();
            var mediaDataCache = new JsonMediaDataCache();

            _screensaverService = new ScreensaverService();
            _jellyseerrService = new JellyseerrService(config);
            _speechService = new WhisperSpeechToTextService(config);

            // Load voice command key from config — cast the integer value directly to Key.
            // Default 0 (Key.None) means voice command is disabled until configured.
            if (int.TryParse(config["VOICE_COMMAND_KEY"], out int vkCode) && vkCode != 0)
                _voiceCommandKey = (Key)vkCode;
            else
                Console.WriteLine("[MainView] VOICE_COMMAND_KEY not configured. Voice-to-text is disabled. " +
                                  "Set BAIRD_LOG_KEYS=true and press the voice button to find the right value.");

            _inputLockDisabled = Environment.GetEnvironmentVariable("BAIRD_ENABLE_INPUT_LOCK")
                ?.Equals("true", StringComparison.OrdinalIgnoreCase) != true;
            if (_inputLockDisabled)
                Console.WriteLine("[MainView] Input lock disabled. Set BAIRD_ENABLE_INPUT_LOCK=true to re-enable.");

            // Create DataService encapsulating providers and history
            _dataService = new DataService(_providers, _historyService, watchlistService, mediaItemCache, mediaDataCache);

            _viewModel = new MainViewModel(config, _dataService, searchHistoryService, _screensaverService, _cecService, _jellyseerrService, _epgService, netflixSession, disneyService, channel4Service);

            DataContext = _viewModel;

            this.AttachedToVisualTree += async (s, e) =>
            {
                // Clear any previously registered subscriptions (e.g. on hot-reload re-attach)
                _visualTreeSubscriptions.Clear();

                Console.WriteLine("[MainView] Attached to visual tree. Starting initialization...");

                Console.WriteLine("[MainView] Initializing screensaver service...");
                // Fire-and-forget: screensaver data only needed after 30min idle timeout
                _ = Task.Run(() => _screensaverService.InitializeAsync());
                SetupIdleTimer();
                SetupInputUnblockFallbackTimer();

                // Yield to let dispatcher process any queued callbacks (e.g. from fire-and-forget tasks)
                await Task.Yield();

                // Subscribe to VideoLayer exit requests
                var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                if (videoLayer != null)
                {
                    videoLayer.ExitRequested += OnVideoLayerExitRequested;
                    _visualTreeSubscriptions.Add(Disposable.Create(() => videoLayer.ExitRequested -= OnVideoLayerExitRequested));
                }

                Console.WriteLine("[MainView] Setting up input handling...");

                // TopLevel for global input hook? Or just hook on UserControl?
                // UserControl KeyDown bubbles, so focusing Root is important.
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);


                if (topLevel != null)
                {
                    // Global Input Handler (Tunneling) to catch wake-up events
                    topLevel.AddHandler(InputElement.KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
                    _visualTreeSubscriptions.Add(Disposable.Create(() =>
                        topLevel.RemoveHandler(InputElement.KeyDownEvent, OnGlobalKeyDown)));

                    topLevel.AddHandler(InputElement.KeyUpEvent, OnGlobalKeyUp, RoutingStrategies.Tunnel);
                    _visualTreeSubscriptions.Add(Disposable.Create(() =>
                        topLevel.RemoveHandler(InputElement.KeyUpEvent, OnGlobalKeyUp)));

                    if (!_mouseEnabled)
                    {
                        // Remote-only: hide the pointer and swallow every mouse event before it reaches a control.
                        // The remote's OK button arrives as the left mouse button, so that one is turned into Enter.
                        topLevel.Cursor = new Cursor(StandardCursorType.None);
                        SwallowPointerEvents(topLevel, InputElement.PointerMovedEvent, InputElement.PointerWheelChangedEvent);
                        topLevel.AddHandler(InputElement.PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);
                        topLevel.AddHandler(InputElement.PointerReleasedEvent, OnGlobalPointerReleased, RoutingStrategies.Tunnel);
                        _visualTreeSubscriptions.Add(Disposable.Create(() =>
                        {
                            topLevel.RemoveHandler(InputElement.PointerPressedEvent, OnGlobalPointerPressed);
                            topLevel.RemoveHandler(InputElement.PointerReleasedEvent, OnGlobalPointerReleased);
                        }));
                    }

                    // Existing InputCoordinator (Bubbling)
                    topLevel.KeyDown += InputCoordinator;
                    _visualTreeSubscriptions.Add(Disposable.Create(() => topLevel.KeyDown -= InputCoordinator));

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
                _visualTreeSubscriptions.Add(
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
                        }));

                // Handle focus transitions when Quick EPG overlay is toggled
                _visualTreeSubscriptions.Add(
                    _viewModel.ObservableForProperty(x => x.IsEpgActive)
                        .Subscribe(change =>
                        {
                            if (change.Value)
                            {
                                Dispatcher.UIThread.Post(() =>
                                {
                                    var epgLayer = this.FindControl<Baird.Controls.EpgOverlayControl>("EpgLayer");
                                    var list = epgLayer?.FindControl<Avalonia.Controls.ListBox>("OverlayChannelsList");
                                    list?.Focus();
                                }, DispatcherPriority.Input);
                            }
                            else if (_viewModel.CurrentPage == null || _viewModel.CurrentPage is ShowingVideoPlayerViewModel)
                            {
                                Dispatcher.UIThread.Post(() =>
                                {
                                    var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                                    var player = videoLayer?.GetPlayer();
                                    player?.Focus();
                                }, DispatcherPriority.Input);
                            }
                        }));

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

                // Apply the render-logging diagnostic flag and re-apply it live whenever it is toggled
                // on the Settings page.
                if (vLayer != null && _viewModel?.Settings != null)
                {
                    var settings = _viewModel.Settings;
                    void ApplyVideoSettings()
                    {
                        var player = vLayer.GetPlayer();
                        if (player == null) return;
                        player.SetRenderLogging(settings.LogRenderDimensions);
                    }

                    ApplyVideoSettings();
                    _visualTreeSubscriptions.Add(settings.ObservableForProperty(x => x.LogRenderDimensions).Subscribe(_ => ApplyVideoSettings()));
                }

                // Subscribe to ActiveItem changes to notify VideoPlayer of current item identity
                _visualTreeSubscriptions.Add(
                    _viewModel.ObservableForProperty(x => x.ActiveItem)
                        .Subscribe(change =>
                        {
                            var item = change.Value;
                            if (vLayer != null && item != null)
                            {
                                var mediaItem = item;
                                vLayer.GetPlayer()?.SetCurrentMediaItem(mediaItem);
                            }
                        }));

                // Hook up 'Down' key from player to OpenMainMenu
                if (vLayer != null)
                {
                    var player = vLayer.GetPlayer();
                    if (player != null)
                    {
                        // Netflix plays in the browser behind Baird: the window goes transparent while it shows.
                        player.NetflixEngine = _viewModel.NetflixEngine;
                        player.AddBrowserEngine(_viewModel.DisneyEngine);
                        player.AddBrowserEngine(_viewModel.Channel4Engine);
                        _visualTreeSubscriptions.Add(player.GetObservable(Controls.VideoPlayer.IsShowingBrowserProperty).Subscribe(showing =>
                        {
                            if (TopLevel.GetTopLevel(this) is not Window window) return;
                            Console.WriteLine($"[MainView] Showing the browser through the main window: {showing}");
                            window.Background = showing ? Avalonia.Media.Brushes.Transparent : Avalonia.Media.Brushes.Black;
                        }));

                        // A/V debug page samples the main player; M while watching drops a mark.
                        _viewModel.AvDebug.Source = player;
                        EventHandler onMarkRequested = (sender, args) => _viewModel.AvDebug.Mark();
                        player.DiagnosticsMarkRequested += onMarkRequested;
                        _visualTreeSubscriptions.Add(Disposable.Create(() => player.DiagnosticsMarkRequested -= onMarkRequested));

                        EventHandler onHistoryRequested = (sender, args) =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => _viewModel.OpenMainMenu());
                        };
                        player.HistoryRequested += onHistoryRequested;
                        _visualTreeSubscriptions.Add(Disposable.Create(() => player.HistoryRequested -= onHistoryRequested));
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

                Console.WriteLine("[MainView] Preloading history, watchlist and guide...");
                // Preload history, watchlist and EPG so they are ready when user opens them
                await _viewModel.History.RefreshAsync();
                await _viewModel.Watchlist.RefreshAsync();
                await _viewModel.Epg.RefreshAsync();

                Console.WriteLine("[MainView] Starting CEC service...");

                // Wire CEC TV power events for auto-pause/resume
                EventHandler onTvStandby = (s, e) => Dispatcher.UIThread.Post(OnTvStandby);
                _cecService.TvStandby += onTvStandby;
                _visualTreeSubscriptions.Add(Disposable.Create(() => _cecService.TvStandby -= onTvStandby));

                EventHandler onTvPowerOn = (s, e) =>
                {
                    // Only reclaim the input if Baird was the intended active source before standby.
                    // "Image View On" / "Text View On" are sent by ANY device waking the TV (e.g. Chromecast),
                    // so we must not steal the input when another device is in use.
                    // NOTE: CEC events fire on a background thread; decision logic stays here.
                    // Only DispatcherTimer operations are marshalled to the UI thread.
                    if (!_weAreIntendedActiveSource)
                    {
                        Console.WriteLine("[MainView] TV power on via CEC — another device is active, not asserting.");
                        if (_inputsBlocked)
                        {
                            Dispatcher.UIThread.Post(() =>
                            {
                                _inputUnblockFallbackTimer?.Stop();
                                _inputUnblockFallbackTimer?.Start();
                            });
                        }
                        return;
                    }
                    Console.WriteLine("[MainView] TV power on via CEC — re-asserting active source.");
                    _ = _cecService.ChangeInputToThisDeviceAsync();
                };
                _cecService.TvPowerOn += onTvPowerOn;
                _visualTreeSubscriptions.Add(Disposable.Create(() => _cecService.TvPowerOn -= onTvPowerOn));

                EventHandler onInputLost = (s, e) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Console.WriteLine("[MainView] Input lost via CEC — another device is now active source.");
                        _weAreIntendedActiveSource = false;
                        if (!_inputLockDisabled) _inputsBlocked = true;
                    });
                };
                _cecService.InputLost += onInputLost;
                _visualTreeSubscriptions.Add(Disposable.Create(() => _cecService.InputLost -= onInputLost));

                EventHandler onInputRegained = (s, e) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Console.WriteLine("[MainView] Input regained via CEC — unblocking inputs.");
                        _weAreIntendedActiveSource = true;
                        UnblockInputs();
                    });
                };
                _cecService.InputRegained += onInputRegained;
                _visualTreeSubscriptions.Add(Disposable.Create(() => _cecService.InputRegained -= onInputRegained));

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
                            _weAreIntendedActiveSource = true;
                            if (!_inputLockDisabled) _inputsBlocked = true;
                            _ = _cecService.ChangeInputToThisDeviceAsync();
                        });
                    }
                });
            };
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _visualTreeSubscriptions.Clear();
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

        private void SetupInputUnblockFallbackTimer()
        {
            _inputUnblockFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _inputUnblockFallbackTimer.Tick += (_, _) =>
            {
                _inputUnblockFallbackTimer.Stop();
                if (_inputsBlocked)
                {
                    Console.WriteLine("[MainView] Fallback timer fired — unblocking inputs without CEC confirmation.");
                    UnblockInputs();
                }
            };
        }

        private void OnTvStandby()
        {
            Console.WriteLine("[MainView] TV standby via CEC — blocking inputs.");
            if (!_inputLockDisabled) _inputsBlocked = true;

            var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
            var player = videoLayer?.GetPlayer();
            if (player != null && player.GetState() == Baird.Mpv.PlaybackState.Playing)
            {
                Console.WriteLine("[MainView] TV standby via CEC — pausing video.");
                player.Pause();
                _pausedForCecStandby = true;
            }
        }

        /// <summary>
        /// The remote's power and volume keys drive the TV over CEC rather than the Pi (configure_pi.sh
        /// removes labwc's bindings for them and stops logind acting on power/sleep).
        /// </summary>
        private bool TryHandleTvKey(KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.VolumeUp:
                    if (_volumeUpGate.ShouldSend(DateTime.Now)) _ = _cecService.VolumeUpAsync();
                    return true;
                case Key.VolumeDown:
                    if (_volumeDownGate.ShouldSend(DateTime.Now)) _ = _cecService.VolumeDownAsync();
                    return true;
                case Key.VolumeMute:
                    if (_muteGate.ShouldSend(DateTime.Now)) _ = _cecService.MuteAsync();
                    return true;
            }

            // XF86PowerOff has no Avalonia Key (it arrives as Key.None), so match the physical key.
            if (e.Key == Key.Sleep || e.PhysicalKey is PhysicalKey.Power or PhysicalKey.Sleep)
            {
                _ = ToggleTvPowerAsync();
                return true;
            }

            return false;
        }

        private async Task ToggleTvPowerAsync()
        {
            // A held power key autorepeats, and the remote reports it on more than one device: one toggle per press.
            if (_tvPowerToggleInFlight || DateTime.Now - _lastTvPowerToggle < TvPowerToggleCooldown) return;
            _tvPowerToggleInFlight = true;
            try
            {
                if (await _cecService.TogglePowerAsync())
                    RequestInputRegain();
                else
                    OnTvStandby(); // The TV doesn't broadcast a standby we asked for, so act on it ourselves.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainView] TV power toggle failed: {ex.Message}");
            }
            finally
            {
                _lastTvPowerToggle = DateTime.Now;
                _tvPowerToggleInFlight = false;
            }
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
            _weAreIntendedActiveSource = true;
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
            _weAreIntendedActiveSource = true;
            _ = _cecService.PowerOnAsync();
            _ = _cecService.ChangeInputToThisDeviceAsync();

            // Restart the pre-allocated fallback timer so repeated presses don't stack timers
            _inputUnblockFallbackTimer?.Stop();
            _inputUnblockFallbackTimer?.Start();
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
            // Before everything else: TV keys must work while inputs are blocked or the screensaver
            // is up, and must not trigger AssertCecPresence's power-on.
            if (TryHandleTvKey(e))
            {
                e.Handled = true;
                return;
            }

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

            // Voice command: hold to record, release to transcribe into the search query.
            // Only activates when focus is inside OmniSearchControl or SeerrchControl.
            if (_voiceCommandKey != Key.None && e.Key == _voiceCommandKey && !_isListeningForVoice)
            {
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
                var focused = topLevel?.FocusManager?.GetFocusedElement() as Visual;
                var searchScreen = (Control?)focused?.FindAncestorOfType<Baird.Controls.OmniSearchControl>()
                                   ?? focused?.FindAncestorOfType<Baird.Controls.SeerrchControl>();
                if (searchScreen?.DataContext is Baird.ViewModels.ISearchTextInput target)
                {
                    _voiceTarget = target;
                    _isListeningForVoice = true;
                    _ = _speechService.StartRecordingAsync();
                    Console.WriteLine("[MainView] Voice command: recording started.");
                    e.Handled = true;
                    return;
                }
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
            // Voice command released: stop recording and transcribe into the captured TextBox.
            if (_voiceCommandKey != Key.None && e.Key == _voiceCommandKey && _isListeningForVoice)
            {
                _isListeningForVoice = false;
                var target = _voiceTarget;
                _voiceTarget = null;
                e.Handled = true;

                _ = Task.Run(async () =>
                {
                    var transcript = await _speechService.StopAndTranscribeAsync();
                    if (transcript != null && target != null)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            target.AppendText(transcript);
                            Console.WriteLine($"[MainView] Voice transcript appended: \"{transcript}\"");
                        });
                    }
                });
                return;
            }

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

        private void SwallowPointerEvents(Avalonia.Controls.TopLevel topLevel, params RoutedEvent[] events)
        {
            foreach (var routedEvent in events)
            {
                topLevel.AddHandler(routedEvent, (EventHandler<RoutedEventArgs>)SwallowPointerEvent, RoutingStrategies.Tunnel);
                _visualTreeSubscriptions.Add(Disposable.Create(() =>
                    topLevel.RemoveHandler(routedEvent, (EventHandler<RoutedEventArgs>)SwallowPointerEvent)));
            }
        }

        private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (e.GetCurrentPoint(null).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
                RaiseEnterOnFocus(InputElement.KeyDownEvent);
        }

        private void OnGlobalPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            e.Handled = true;
            if (e.InitialPressMouseButton == MouseButton.Left)
                RaiseEnterOnFocus(InputElement.KeyUpEvent);
        }

        private void RaiseEnterOnFocus(RoutedEvent<KeyEventArgs> keyEvent)
        {
            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
            var target = topLevel?.FocusManager?.GetFocusedElement() as Interactive ?? topLevel;
            target?.RaiseEvent(new KeyEventArgs { RoutedEvent = keyEvent, Key = Key.Enter, Source = target });
        }

        private static void SwallowPointerEvent(object? sender, RoutedEventArgs e) => e.Handled = true;

        private void InputCoordinator(object? sender, KeyEventArgs e)
        {
            // If the event was already handled (e.g. by a focused TextBox or OnGlobalKeyDown), don't trigger global logic
            if (e.Handled) return;

            // Reset HUD Timer on any interaction
            _viewModel.ResetHudTimer();

            // Key diagnostics — set BAIRD_LOG_KEYS=true in config.ini to find voice button key codes.
            Console.WriteLine($"[MainView] Key: {e.Key} ({(int)e.Key}), physical {e.PhysicalKey}");

            // Back/Esc Trigger
            if (e.Key == Key.Escape || e.Key == Key.BrowserBack)
            {
                HandleBackTrigger(e);
                e.Handled = true; // Always consume Back/Esc
                return;
            }

            // HomePage key — close all menus and return to video player
            if (e.Key == Key.BrowserHome)
            {
                _viewModel.IsEpgActive = false;
                _viewModel.GoHome();
                e.Handled = true;
                return;
            }

            // EPG / Guide Trigger (G key or MediaTvGuide)
            if (e.Key == Key.G || (int)e.Key == 100026)
            {
                _viewModel.ToggleEpgOverlay();
                e.Handled = true;
                return;
            }

            // Q key is now handled by VideoPlayer


        }

        private void HandleBackTrigger(KeyEventArgs e)
        {
            if (_viewModel.IsEpgActive)
            {
                _viewModel.IsEpgActive = false;
                e.Handled = true;
                return;
            }

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
