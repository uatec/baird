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
                // TopLevel for global input hook? Or just hook on UserControl?
                // UserControl KeyDown bubbles, so focusing Root is important.
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
                if (topLevel != null)
                {
                    topLevel.KeyDown += InputCoordinator;
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
                     _viewModel.PlayItem(firstChannel);
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

            // Reset HUD Timer on any interaction
            _viewModel.ResetHudTimer();

            // Debug key press
            Console.WriteLine($"Key: {e.Key}");



            // Numeric Triggers (0-9)
            if (IsNumericKey(e.Key))
            {
                HandleNumericTrigger(e);
                return;
            }



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
                Environment.Exit(0);
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

            if (!(_viewModel.CurrentPage is OmniSearchViewModel))
            {
                // Activate Search Mode
                _viewModel.OmniSearch.Clear();
                _viewModel.PushViewModel(_viewModel.OmniSearch);
                
                // Focus Search Box handled by OmniSearchControl.AttachedToVisualTree
                
                // Append digit
                _viewModel.OmniSearch.SearchText = digit;
                
                e.Handled = true; // Consume this initial key
            }


            // If search IS active, we do nothing. 
        }





        private void HandleBackTrigger(KeyEventArgs e)
        {
            // If there is any page open, go back
            if (_viewModel.CurrentPage != null)
            {
                _viewModel.GoBack();
                e.Handled = true;
            }
        }

        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        }
    }
}
