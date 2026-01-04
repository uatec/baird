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
        private Dictionary<string, IMediaProvider> _itemProviderMap = new();

        public MainView()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
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
                        _viewModel.IsSearchActive = false;
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

                await InitializeMediaProvider();
            };
        }

        private async Task InitializeMediaProvider()
        {
             _providers.Clear();
             _itemProviderMap.Clear();
             
             _providers.Add(new TvHeadendService());
             _providers.Add(new JellyfinService());

             try
             {
                 var allItems = new List<MediaItem>();

                 foreach (var provider in _providers)
                 {
                     try 
                     {
                         await provider.InitializeAsync();
                         var items = await provider.GetListingAsync();
                         
                         foreach (var item in items)
                         {
                             // Map ID to Provider for playback
                             if (!_itemProviderMap.ContainsKey(item.Id))
                             {
                                 _itemProviderMap[item.Id] = provider;
                                 allItems.Add(item);
                             }
                         }
                     }
                     catch (Exception ex)
                     {
                         Console.WriteLine($"Error init provider {provider.GetType().Name}: {ex}");
                     }
                 }

                 // log loaded channel count
                 Console.WriteLine($"Loaded: {allItems.Count} total items");
                 
                 // Store items in ViewModel Source
                 _viewModel.OmniSearch.AllItems = allItems;
                 
                 // Trigger initial display (Show All)
                 _viewModel.OmniSearch.Clear();

                 // Auto-play first channel > 0 from TVH (assuming Details is channel num)
                 var firstChannel = allItems
                    .Where(i => i.Details != "0" && int.TryParse(i.Details, out _)) // Simple check for numeric channel
                    .OrderBy(i => i.Details.Length).ThenBy(i => i.Details) // Sort by logical order ish
                    .FirstOrDefault();

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

            if (!_viewModel.IsSearchActive)
            {
                // Activate Search Mode
                _viewModel.IsSearchActive = true;
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
            if (_itemProviderMap.TryGetValue(item.Id, out var provider))
            {
                var url = provider.GetStreamUrl(item.Id);
                Console.WriteLine($"Activating Item: {item.Name} at {url}");
                
                _viewModel.ActiveItem = new ActiveMedia 
                {
                    Name = item.Name,
                    Details = item.Details,
                    StreamUrl = url,
                    IsLive = item.IsLive
                };
            }
            else
            {
                Console.WriteLine($"Error: No provider found for item {item.Name} ({item.Id})");
            }
        }

        private void HandleUpTrigger(KeyEventArgs e)
        {
            // Logic: If on BaseLayer (Video/Home) and press Up -> Open Search with Keyboard
            
            if (!_viewModel.IsSearchActive)
            {
                 _viewModel.IsSearchActive = true;
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
                // Hide Keyboard, Keep Search Open
                _viewModel.OmniSearch.IsKeyboardVisible = false;
                
                // Focus Results
                Dispatcher.UIThread.Post(() => 
                {
                    var searchControl = this.FindControl<Baird.Controls.OmniSearchControl>("OmniSearchLayer");
                    searchControl?.FocusResults();
                });
            }
            else if (_viewModel.IsSearchActive)
            {
                // Close Search, Return to Base
                _viewModel.IsSearchActive = false;
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
