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

namespace Baird
{
    public partial class MainView : UserControl
    {
        private MainViewModel _viewModel;
        // private IMediaProvider _mediaProvider; // Removed single provider
        private List<IMediaProvider> _providers = new();
        private ICecService _cecService;
        private IHistoryService _historyService;
        private IDataService _dataService;

        public MainView()
        {
            InitializeComponent();

            _providers.Add(new TvHeadendService());
            _providers.Add(new JellyfinService());
            _providers.Add(new BbcIPlayerService());
            _providers.Add(new YouTubeService());

            _cecService = new CecService();
            _historyService = new JsonHistoryService();
            var searchHistoryService = new SearchHistoryService();

            // Create DataService encapsulating providers and history
            _dataService = new DataService(_providers, _historyService);

            _viewModel = new MainViewModel(_dataService, searchHistoryService);

            DataContext = _viewModel;

            this.AttachedToVisualTree += async (s, e) =>
            {
                // TopLevel for global input hook? Or just hook on UserControl?
                // UserControl KeyDown bubbles, so focusing Root is important.
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
                if (topLevel != null)
                {
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
                            // ActiveMedia is a subclass or similar to MediaItem? 
                            // Wait, ActiveMedia is defined where? In MainViewModel.cs?
                            // Let's check MainViewModel.cs for ActiveMedia definition.
                            // It seems to be a local class or struct, or reusing MediaItem.

                            // If ActiveMedia is compatible or we can map it.
                            // MainViewModel.cs: private ActiveMedia? _activeItem;
                            // We probably need to map it back to MediaItem or just pass the ID/Name/etc.
                            // VideoPlayer.SetCurrentMediaItem takes MediaItem.

                            // Let's assume for now we construct a MediaItem from ActiveMedia
                            //  var mediaItem = new MediaItem 
                            //  {
                            //      Id = item.Id,
                            //      Name = item.Name,
                            //      Details = item.Details,
                            //      ImageUrl = item.ImageUrl,
                            //      Source = item.Source,
                            //      Type = item.Type,
                            //      Synopsis = item.Synopsis,
                            //      Subtitle = item.Subtitle,
                            //      IsLive = item.IsLive,
                            //      StreamUrl = item.StreamUrl
                            //  };
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

        private void InputCoordinator(object? sender, KeyEventArgs e)
        {
            // If the event was already handled (e.g. by a focused TextBox), don't trigger global logic
            if (e.Handled) return;

            // Reset HUD Timer on any interaction
            _viewModel.ResetHudTimer();

            // Debug key press
            Console.WriteLine($"Key: {e.Key}");







            // Channel Navigation
            if (e.Key == Key.OemPlus || e.Key == Key.Add || e.Key == Key.MediaNextTrack || e.Key == Key.PageUp)
            {
                _viewModel.SelectNextChannel();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.OemMinus || e.Key == Key.Subtract || e.Key == Key.MediaPreviousTrack || e.Key == Key.PageDown)
            {
                _viewModel.SelectPreviousChannel();
                e.Handled = true;
                return;
            }

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
                // UpsertAsync writes to file.
                // We should probably wait a bit or make SaveProgress synchronous-ish or blocking?
                // But SaveProgress is async void.
                // For now, let's just call it and sleep slightly.
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
