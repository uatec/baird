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
        private TextBox? _voiceTargetTextBox;

        // Visual-tree subscription lifetime tracking
        private readonly CompositeDisposable _visualTreeSubscriptions = new();

        // Screensaver & Idle
        private ScreensaverService? _screensaverService;
        private DispatcherTimer? _idleTimer;
        private bool _wasPausedForScreensaver = false; // Track if we actively paused for screensaver
        private bool _pausedForCecStandby = false; // Track if we auto-paused because TV went to standby
        private volatile bool _inputsBlocked = false; // Inputs are blocked when TV is off or on a different input
        private readonly bool _inputLockDisabled = true; // Set BAIRD_DISABLE_INPUT_LOCK=true to bypass input blocking (for debugging flaky CEC lockout)
        private volatile bool _weAreIntendedActiveSource = false; // True when Baird should be the TV's active source; false when another device (e.g. Chromecast) is in use
        private DispatcherTimer? _inputUnblockFallbackTimer; // Fallback unblock when TV switches silently (no Request Active Source)
        private DateTime _lastCecAssert = DateTime.MinValue;
        private static readonly TimeSpan CecAssertCooldown = TimeSpan.FromSeconds(30);

        // Voice command key — loaded from config (VOICE_COMMAND_KEY).
        // Avalonia's Key enum integers do NOT correspond to evdev keycodes; they go through
        // XKB → keysym → Avalonia Key. Because KEY_VOICECOMMAND (evdev 0x246) is unmapped in
        // most XKB tables it may arrive as Key.None or an unexpected value.
        // To discover the real value: set BAIRD_LOG_KEYS=true in config.ini and watch the console
        // while pressing the voice button, then set VOICE_COMMAND_KEY to the logged integer.
        private Key _voiceCommandKey = Key.None;

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

            _viewModel = new MainViewModel(config, _dataService, searchHistoryService, _screensaverService, _cecService, _jellyseerrService, _epgService);

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

                    topLevel.AddHandler(InputElement.PointerPressedEvent, OnGlobalPointerActivity, RoutingStrategies.Tunnel);
                    _visualTreeSubscriptions.Add(Disposable.Create(() =>
                        topLevel.RemoveHandler(InputElement.PointerPressedEvent, OnGlobalPointerActivity)));

                    topLevel.AddHandler(InputElement.PointerMovedEvent, OnGlobalPointerActivity, RoutingStrategies.Tunnel);
                    _visualTreeSubscriptions.Add(Disposable.Create(() =>
                        topLevel.RemoveHandler(InputElement.PointerMovedEvent, OnGlobalPointerActivity)));

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

                Console.WriteLine("[MainView] Preloading history and watchlist...");
                // Preload history so it's ready when user opens it
                await _viewModel.History.RefreshAsync();
                await _viewModel.Watchlist.RefreshAsync();

                Console.WriteLine("[MainView] Starting CEC service...");

                // Wire CEC TV power events for auto-pause/resume
                EventHandler onTvStandby = (s, e) =>
                {
                    Dispatcher.UIThread.Post(() =>
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
                    });
                };
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

            // Voice command: hold to record, release to transcribe into the search box.
            // Only activates when focus is on the SearchBox inside OmniSearchControl or SeerrchControl.
            if (_voiceCommandKey != Key.None && e.Key == _voiceCommandKey && !_isListeningForVoice)
            {
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
                var focused = topLevel?.FocusManager?.GetFocusedElement() as TextBox;
                if (focused?.Name == "SearchBox" &&
                    (focused.FindAncestorOfType<Baird.Controls.OmniSearchControl>() != null ||
                     focused.FindAncestorOfType<Baird.Controls.SeerrchControl>() != null))
                {
                    _voiceTargetTextBox = focused;
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
                var target = _voiceTargetTextBox;
                _voiceTargetTextBox = null;
                e.Handled = true;

                _ = Task.Run(async () =>
                {
                    var transcript = await _speechService.StopAndTranscribeAsync();
                    if (transcript != null && target != null)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            target.Text = (target.Text ?? string.Empty) + transcript;
                            target.CaretIndex = target.Text.Length;
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

            // Key diagnostics — set BAIRD_LOG_KEYS=true in config.ini to find voice button key codes.
            Console.WriteLine($"[MainView] Key: {e.Key} ({(int)e.Key})");

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
                _viewModel.GoHome();
                e.Handled = true;
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
