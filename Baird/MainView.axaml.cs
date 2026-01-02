using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;

namespace Baird
{
    public partial class MainView : UserControl
    {
        private DispatcherTimer _timer;
        private TextBlock? _clockBlock;

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
                 // Auto-play on startup
                 var player = this.FindControl<Baird.Controls.VideoPlayer>("Player");
                 player?.Play("http://commondatastorage.googleapis.com/gtv-videos-bucket/sample/ElephantsDream.mp4");
            };
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
