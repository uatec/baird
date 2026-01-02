using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Baird.Services;
using System;
using System.Linq;

namespace Baird
{
    public partial class MainView : UserControl
    {
        private DispatcherTimer _timer;
        private TextBlock? _clockBlock;
        private IMediaProvider _mediaProvider;

        public MainView()
        {
            InitializeComponent();
            
            _clockBlock = this.FindControl<TextBlock>("ClockBlock");
            
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
            _timer.Start();

            // Initial update
            UpdateClock();

            this.AttachedToVisualTree += (s, e) =>
            {
                 // Initialize Media Service (TVHeadend)
                 InitializeMediaProvider();
            };
        }
        
        private async void InitializeMediaProvider()
        {
            // Switch to TvHeadendService
            // _mediaProvider = new TvHeadendService();
            _mediaProvider = new JellyfinService(); // Easy to switch back
            
            var statusBlock = this.FindControl<TextBlock>("StatusTextBlock");
            if (statusBlock != null) statusBlock.Text = "Loading configuration...";

            // LoadEnv() call removed, services now handle their own .env/config loading internally

            try 
            {
                await _mediaProvider.InitializeAsync();
                
                if (statusBlock != null) statusBlock.Text = "Fetching channels...";
                var items = await _mediaProvider.GetListingAsync();
                
                var movieList = this.FindControl<ListBox>("MovieList");
                if (movieList != null)
                {
                    var itemList = items.ToList();
                    Console.WriteLine($"Found {itemList.Count} channels.");
                    movieList.ItemsSource = itemList;
                    movieList.SelectionChanged += OnItemSelected;

                    if (itemList.Count == 0) 
                    {
                        if (statusBlock != null) statusBlock.Text = "No channels found.";
                    }
                    else
                    {
                         if (statusBlock != null) statusBlock.Text = ""; // Clear status on success
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Media Init Error: {ex}");
                if (statusBlock != null) statusBlock.Text = $"Error: {ex.Message}";
            }
        }

        private void LoadEnv()
        {
            try {
                var path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env");
                // Attempt to find .env in project root if running from bin
                if (!System.IO.File.Exists(path))
                {
                     path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../../.env");
                }
                
                if (System.IO.File.Exists(path))
                {
                    foreach (var line in System.IO.File.ReadAllLines(path))
                    {
                        var parts = line.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            Environment.SetEnvironmentVariable(parts[0].Trim(), parts[1].Trim());
                        }
                    }
                    Console.WriteLine("Loaded .env file");
                }
                else
                {
                    Console.WriteLine("No .env file found");
                }
            } catch { /* ignore */ }
        }
        
        private void OnItemSelected(object? sender, SelectionChangedEventArgs e)
        {
            var listBox = sender as ListBox;
            if (listBox?.SelectedItem is MediaItem item)
            {
                var url = _mediaProvider.GetStreamUrl(item.Id);
                Console.WriteLine($"Playing Channel: {item.Name} at {url}");
                
                var player = this.FindControl<Baird.Controls.VideoPlayer>("Player");
                player?.Play(url);
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            UpdateClock();
        }

        private void UpdateClock()
        {
             if (_clockBlock != null)
             {
                 _clockBlock.Text = DateTime.Now.ToString("HH:mm:ss");
             }
             
             var player = this.FindControl<Baird.Controls.VideoPlayer>("Player");
             var debugBlock = this.FindControl<TextBlock>("DebugInfo");
             
             if (player != null && debugBlock != null)
             {
                 debugBlock.Text = $"State: {player.GetState()}\nURL: {player.GetCurrentPath()}\nMPV Paused: {player.IsMpvPaused}\nPos: {player.GetTimePos()} / {player.GetDuration()}";
                 
                 // Update Pause Button Text
                 var pauseBtn = this.FindControl<Button>("PauseButton");
                 if (pauseBtn != null)
                 {
                     pauseBtn.Content = player.IsMpvPaused ? "Resume" : "Pause";
                 }
             }
        }

        public void OnPlayClick(object sender, RoutedEventArgs e)
        {
            var player = this.FindControl<Baird.Controls.VideoPlayer>("Player");
            player?.Play("http://commondatastorage.googleapis.com/gtv-videos-bucket/sample/ElephantsDream.mp4");
        }

        public void OnPauseClick(object sender, RoutedEventArgs e)
        {
             var player = this.FindControl<Baird.Controls.VideoPlayer>("Player");
             if (player != null)
             {
                 if (player.IsMpvPaused)
                    player.Resume();
                 else
                    player.Pause();
             }
        }

        public void OnStopClick(object sender, RoutedEventArgs e)
        {
             var player = this.FindControl<Baird.Controls.VideoPlayer>("Player");
             player?.Stop();
        }

        public void OnExitClick(object sender, RoutedEventArgs e)
        {
            // Close the application
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
            else if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView)
            {
                 // For Framebuffer/Mobile where there is single view lifetime,
                 // currently there isn't a direct Shutdown() on the interface in some versions, 
                 // but typically we can exit the process or main loop.
                 // However, for correct Avalonia lifecycle, let's try to get the platform to exit if possible,
                 // or just Environment.Exit(0) as a fallback for this simple app.
                 Environment.Exit(0);
            }
        }

        private void InitializeComponent()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        }
    }
}
