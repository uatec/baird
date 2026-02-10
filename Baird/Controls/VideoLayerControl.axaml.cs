using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Baird.Controls
{
    public partial class VideoLayerControl : UserControl
    {
        public VideoLayerControl()
        {
            InitializeComponent();
        }
        
        public Baird.Services.IHistoryService? HistoryService
        {
            get => GetPlayer()?.HistoryService;
            set 
            {
                var player = GetPlayer();
                if (player != null) player.HistoryService = value;
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
        
        // Expose Player for external control if needed, or binding
        public VideoPlayer? GetPlayer()
        {
            return this.FindControl<VideoPlayer>("Player");
        }

        protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            var player = GetPlayer();
            if (player != null)
            {
                player.SearchRequested += OnSearchRequested;
                player.UserActivity += OnUserActivity;
                player.StreamEnded += OnStreamEnded;
                player.Focus(); // Ensure player gets focus when layer is active
            }
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
             var player = GetPlayer();
            if (player != null)
            {
                player.SearchRequested -= OnSearchRequested;
                player.UserActivity -= OnUserActivity;
                player.StreamEnded -= OnStreamEnded;
            }
        }

        private void OnUserActivity(object? sender, EventArgs e)
        {
             if (DataContext is ViewModels.MainViewModel vm)
             {
                 vm.ResetHudTimer();
             }
        }

        private void OnSearchRequested(object? sender, string text)
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.OmniSearch.Clear();
                vm.PushViewModel(vm.OmniSearch);
                
                if (!string.IsNullOrEmpty(text))
                {
                    vm.OmniSearch.SearchText = text;
                }
            }
        }

        private void OnStreamEnded(object? sender, EventArgs e)
        {
            Console.WriteLine("[VideoLayerControl] StreamEnded event received, navigating back");
            
            // Ensure we're on the UI thread before calling navigation
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is ViewModels.MainViewModel vm)
                {
                    Console.WriteLine("[VideoLayerControl] Calling PopViewModel to navigate back");
                    vm.PopViewModel();
                }
            });
        }
    }
}
