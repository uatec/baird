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
        private ScreensaverService _screensaverService;
        private DispatcherTimer _idleTimer;

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

            this.AttachedToVisualTree += async (s, e) =>
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
                            // When transitioning between overlay pages, temporarily focus the
                            // PageFrame to prevent VideoPlayer from capturing focus while the
                            // new control's FocusFirstItem() waits for data to load.
                            Dispatcher.UIThread.Post(() =>
                            {
                                var pageFrame = this.FindControl<ContentControl>("PageFrame");
                                if (pageFrame != null)
                                {
                                    Console.WriteLine("[MainView] CurrentPage changed to non-null, parking focus on PageFrame");
                                    pageFrame.Focusable = true;
                                    pageFrame.Focus();
                                }
                            });
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

                // Hook up 'Down' key from player to OpenHistory
                if (vLayer != null)
                {
                    var player = vLayer.GetPlayer();
                    if (player != null)
                    {
                        player.HistoryRequested += (sender, args) =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => _viewModel.OpenHistory());
                        };
                    }
                }


                await _viewModel.OmniSearch.ClearAndSearch();
                await _viewModel.RefreshChannels();

                // Auto-play first channel
                var firstChannel = _viewModel.AllChannels.FirstOrDefault();
                if (firstChannel != null)
                {
                    Console.WriteLine($"Auto-playing channel: {firstChannel.Name}");
                    _viewModel.PlayItem(firstChannel);
                }

                await _cecService.StartAsync();
            };
        }

        private void SetupIdleTimer()
        {
            _idleTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
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
            Console.WriteLine($"Key: {e.Key}");

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
                var vLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
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
}
