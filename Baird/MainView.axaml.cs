using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Baird.Services;
using Baird.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Baird
{
    public partial class MainView : UserControl
    {
        private MainViewModel _viewModel;
        // private IMediaProvider _mediaProvider; // Removed single provider
        private List<IMediaProvider> _providers = new();

        public MainView()
        {
            InitializeComponent();

            _providers.Add(new TvHeadendService());
            _providers.Add(new JellyfinService());
            _providers.Add(new YouTubeService());
            _providers.Add(new BbcIPlayerService());

            _viewModel = new MainViewModel(_providers);
            
            DataContext = _viewModel;

            this.AttachedToVisualTree += async (s, e) =>
            {
                // Hook up OmniSearch Play Event
                var searchControl = this.FindControl<Baird.Controls.OmniSearchControl>("OmniSearchLayer");
                if (searchControl != null)
                {
                    searchControl.ItemChosen += (sender, item) => 
                    {
                        PlayItem(item);
                        // Close search
                        _viewModel.OmniSearch.IsSearchActive = false;
                        _viewModel.OmniSearch.Clear();
                    };
                }

                // TopLevel for global input hook? Or just hook on UserControl?
                // UserControl KeyDown bubbles, so focusing Root is important.
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
                if (topLevel != null)
                {
                    topLevel.KeyDown += InputCoordinator;
                }
                
                // Focus the Base Layer initially
                // this.FindControl<Grid>("BaseLayer")?.Focus(); // Grids aren't focusable by default usually, might need a focusable element

                // Hook up ProgrammeDetail Control Events
                var detailControl = this.FindControl<Baird.Controls.ProgrammeDetailControl>("ProgrammeDetailLayer");
                if (detailControl != null)
                {
                    detailControl.EpisodeChosen += (sender, item) => 
                    {
                        PlayItem(item);
                        // Optional: Keep detail view open or close it? 
                        // Usually playing video overlays everything, so maybe okay.
                        // But if we want to return to detail view after video ends, we shouldn't close it here.
                        // However, PlayItem logic currently sets ActiveItem which replaces the video layer content?
                        // Actually ActiveItem data binding in VideoLayerControl will start playback.
                        // We can leave the detail view "visible" behind the video or overlay?
                        // VideoLayer is Layer 0, ProgrammeDetail is Layer 3.
                        // If ProgrammeDetail is visible (Opacity 1), it covers Layer 0.
                        // So we MUST close or hide ProgrammeDetail to see the video.
                        // Or we need a Player Overlay on top of everything.
                        
                        // For now, let's close it to be safe and simple.
                        _viewModel.CloseProgramme();
                    };

                    detailControl.BackRequested += (sender, args) =>
                    {
                        _viewModel.CloseProgramme();
                        // Focus Search if it was open? Or just reset?
                        // If we came from Search, restoring Search would be nice.
                        if(_viewModel.OmniSearch.IsSearchActive)
                        {
                             Dispatcher.UIThread.Post(() => 
                             {
                                 var searchControl = this.FindControl<Baird.Controls.OmniSearchControl>("OmniSearchLayer");
                                 searchControl?.FocusResults();
                             });
                        }
                    };
                }

                await InitializeMediaProvider();
            };
        }

        private async Task InitializeMediaProvider()
        {
             try
             {
                 foreach (var provider in _providers)
                 {
                     try 
                     {
                         await provider.InitializeAsync();
                     }
                     catch (Exception ex)
                     {
                         Console.WriteLine($"Error init provider {provider.GetType().Name}: {ex}");
                     }
                 }

                 // Trigger initial "Search" with empty query to populate results list
                 await _viewModel.OmniSearch.ClearAndSearch();

                 // Initial Channel Refresh
                 await _viewModel.RefreshChannels();

                 // Auto-play first channel logic
                 // Use the ViewModel's aggregated list
                 var firstChannel = _viewModel.AllChannels.FirstOrDefault();

                 if (firstChannel != null)
                 {
                     Console.WriteLine($"Auto-playing channel: {firstChannel.Name}");
                     PlayItem(firstChannel);
                 }
             }
             catch(Exception ex)
             {
                 Console.WriteLine($"Error init media: {ex}");
             }
        }

        private void InputCoordinator(object? sender, KeyEventArgs e)
        {
            // If the event was already handled (e.g. by a focused TextBox), don't trigger global logic
            if (e.Handled) return;

            // Debug key press
            Console.WriteLine($"Key: {e.Key}");

            // Numeric Triggers (0-9)
            if (IsNumericKey(e.Key))
            {
                HandleNumericTrigger(e);
                return;
            }

            // Up Trigger
            if (e.Key == Key.Up)
            {
                HandleUpTrigger(e);
                // Don't mark handled generally, as it might be needed for nav, BUT
                // if we just switched layers, we might want to consume it.
                return;
            }

            // Channel Navigation
            if (e.Key == Key.OemPlus || e.Key == Key.Add || e.Key == Key.MediaNextTrack)
            {
                _viewModel.SelectNextChannel();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.OemMinus || e.Key == Key.Subtract || e.Key == Key.MediaPreviousTrack)
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
        }

        private bool IsNumericKey(Key key)
        {
            return (key >= Key.D0 && key <= Key.D9) || (key >= Key.NumPad0 && key <= Key.NumPad9);
        }

        private string GetNumericChar(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9) return ((int)key - (int)Key.D0).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return ((int)key - (int)Key.NumPad0).ToString();
            return "";
        }

        private void HandleNumericTrigger(KeyEventArgs e)
        {
            var digit = GetNumericChar(e.Key);

            if (!_viewModel.OmniSearch.IsSearchActive)
            {
                // Activate Search Mode
                _viewModel.OmniSearch.Clear();
                _viewModel.OmniSearch.IsSearchActive = true;
                _viewModel.OmniSearch.IsKeyboardVisible = false; 
                
                // Append Digit to ViewModel
                _viewModel.OmniSearch.AppendDigit(digit);

                // Focus Search Box (so subsequent keys type naturally)
                Dispatcher.UIThread.Post(() => 
                {
                    var searchControl = this.FindControl<Baird.Controls.OmniSearchControl>("OmniSearchLayer");
                    searchControl?.FocusSearchBox();
                });
                
                e.Handled = true; // Consume this initial key
            }
            // If search IS active, we do nothing. 
            // The focused SearchBox (if focused) will handle the key bubbling up to it (or down to it? Bubbling is up, Tunneling is down. KeyDown bubbles).
            // Actually, if SearchBox is focused, it gets the event FIRST. It handles it. 
            // Then InputCoordinator sees it. Conditional at top (e.Handled) will exit.
            // If Search is active but SearchBox NOT focused (e.g. Results focused), then we might want this trigger? 
            // User said: "After that it should not act, because the search box is visible and focused".
            // So we assume it stays focused.
        }

        private void PlayItem(MediaItem item)
        {
            Console.WriteLine($"Playing Item: {item.Name} ({item.Id} - {item.Type})");

            if (item.Type == MediaType.Brand)
            {
                Console.WriteLine($"Opening Programme Detail: {item.Name}");
                
                // Pause background video
                var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                videoLayer?.GetPlayer()?.Pause();
                
                _viewModel.OpenProgramme(item);
                
                // Also close search if active
                 _viewModel.OmniSearch.IsSearchActive = false;
                 _viewModel.OmniSearch.Clear();
                return;
            }
            
            var url = item.StreamUrl;
            if (!string.IsNullOrEmpty(url))
            {
                Console.WriteLine($"Activating Item: {item.Name} at {url}");
                
                _viewModel.ActiveItem = new ActiveMedia 
                {
                    Id = item.Id,
                    Name = item.Name,
                    Details = item.Details,
                    StreamUrl = url,
                    IsLive = item.IsLive,
                    ChannelNumber = item.ChannelNumber
                };
            }
            else
            {
                Console.WriteLine($"Warning: No stream URL for {item.Name}");
            }
        }

        private void HandleUpTrigger(KeyEventArgs e)
        {
            // Logic: If on BaseLayer (Video/Home) and press Up -> Open Search with Keyboard
            
            if (!_viewModel.OmniSearch.IsSearchActive && !_viewModel.IsProgrammeDetailVisible)
            {
                 _viewModel.OmniSearch.Clear();
                 _viewModel.OmniSearch.IsSearchActive = true;
                 _viewModel.OmniSearch.IsKeyboardVisible = true;
                 
                 // Focus Keyboard
                 Dispatcher.UIThread.Post(() => 
                 {
                     var searchControl = this.FindControl<Baird.Controls.OmniSearchControl>("OmniSearchLayer");
                     searchControl?.FocusSearchBox();
                 });
                 
                 e.Handled = true;
            }
            else
            {
                // In search mode, Up might navigate from Results to Keyboard
                if (!_viewModel.OmniSearch.IsKeyboardVisible)
                {
                     // Logic checks focus... complicated without precise focus tracking.
                }
            }
        }

        private void HandleBackTrigger(KeyEventArgs e)
        {
            if (_viewModel.OmniSearch.IsKeyboardVisible)
            {
                // New logic: If search is empty, exit search entirely
                if (string.IsNullOrEmpty(_viewModel.OmniSearch.SearchText))
                {
                    _viewModel.OmniSearch.IsSearchActive = false;
                    _viewModel.OmniSearch.Clear();
                    return;
                }

                // Hide Keyboard, Keep Search Open
                _viewModel.OmniSearch.IsKeyboardVisible = false;
                
                // Focus Results
                Dispatcher.UIThread.Post(() => 
                {
                    var searchControl = this.FindControl<Baird.Controls.OmniSearchControl>("OmniSearchLayer");
                    searchControl?.FocusResults();
                });
            }
            else if (_viewModel.IsProgrammeDetailVisible)
            {
                _viewModel.CloseProgramme();
                // Return to Search if it was active?
                if (_viewModel.OmniSearch.IsSearchActive)
                {
                     Dispatcher.UIThread.Post(() => 
                     {
                         var searchControl = this.FindControl<Baird.Controls.OmniSearchControl>("OmniSearchLayer");
                         searchControl?.FocusResults();
                     });
                }
            }
            else if (_viewModel.OmniSearch.IsSearchActive)
            {
                // Close Search, Return to Base
                _viewModel.OmniSearch.IsSearchActive = false;
                _viewModel.OmniSearch.Clear();

                // Focus Base (Video)
                var videoLayer = this.FindControl<Baird.Controls.VideoLayerControl>("VideoLayer");
                // Focus the player inside if needed, or just the container
                // videoLayer?.GetPlayer()?.Focus();
            }
        }

        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        }
    }
}
