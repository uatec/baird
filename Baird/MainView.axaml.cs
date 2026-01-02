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
        private JellyfinService _jellyfinService;

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
                 // Initialize Jellyfin Service
                 InitializeJellyfin();
                 
                 // Auto-play default (optional, can be removed if specific movie selected)
                 // var player = this.FindControl<Baird.Controls.VideoPlayer>("Player");
                 // player?.Play("http://commondatastorage.googleapis.com/gtv-videos-bucket/sample/ElephantsDream.mp4");
            };
        }
        
        private async void InitializeJellyfin()
        {
            _jellyfinService = new JellyfinService();
            var statusBlock = this.FindControl<TextBlock>("StatusTextBlock");
            if (statusBlock != null) statusBlock.Text = "Loading .env...";

            // Simple .env loader
            LoadEnv();

            // Use Environment Variables or Defaults
            string url = Environment.GetEnvironmentVariable("JELLYFIN_URL") ?? "http://demo.jellyfin.org/stable";
            string user = Environment.GetEnvironmentVariable("JELLYFIN_USER") ?? "demo";
            string pass = Environment.GetEnvironmentVariable("JELLYFIN_PASS") ?? "";

            Console.WriteLine($"Attempting connection to: {url} as {user}");
            if (statusBlock != null) statusBlock.Text = $"Connecting to {url}...";

            try 
            {
                await _jellyfinService.InitializeAsync(url, user, pass);
                
                if (statusBlock != null) statusBlock.Text = "Fetching movies...";
                var movies = await _jellyfinService.GetMoviesAsync();
                
                var movieList = this.FindControl<ListBox>("MovieList");
                if (movieList != null)
                {
                    var items = movies.ToList();
                    Console.WriteLine($"Found {items.Count} movies.");
                    movieList.ItemsSource = items;
                    movieList.SelectionChanged += OnMovieSelected;

                    if (items.Count == 0) 
                    {
                        if (statusBlock != null) statusBlock.Text = "No movies found.";
                    }
                    else
                    {
                         if (statusBlock != null) statusBlock.Text = ""; // Clear status on success
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Jellyfin Init Error: {ex}");
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
        
        private void OnMovieSelected(object? sender, SelectionChangedEventArgs e)
        {
            var movieList = sender as ListBox;
            if (movieList?.SelectedItem is MovieItem movie)
            {
                var url = _jellyfinService.GetStreamUrl(movie.Id);
                Console.WriteLine($"Playing Movie: {movie.Name} at {url}");
                
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
