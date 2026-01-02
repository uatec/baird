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
