using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.Configuration;

namespace Baird.Controls;

public partial class VideoLayerControl : UserControl
{
    public VideoLayerControl()
    {
        InitializeComponent();
    }

    public Baird.Services.IDataService? DataService
    {
        get => GetPlayer()?.DataService;
        set
        {
            VideoPlayer? player = GetPlayer();
            if (player != null)
            {
                player.DataService = value;
            }
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
        VideoPlayer? player = GetPlayer();
        if (player != null)
        {
            player.SearchRequested += OnSearchRequested;
            player.UserActivity += OnUserActivity;
            player.StreamEnded += OnStreamEnded;
            player.ConfigurationToggleRequested += OnConfigurationToggleRequested;
            player.Focus(); // Ensure player gets focus when layer is active
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        VideoPlayer? player = GetPlayer();
        if (player != null)
        {
            player.SearchRequested -= OnSearchRequested;
            player.UserActivity -= OnUserActivity;
            player.StreamEnded -= OnStreamEnded;
            player.ConfigurationToggleRequested -= OnConfigurationToggleRequested;
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
        Console.WriteLine("[VideoLayerControl] StreamEnded event received, checking for next episode");

        // Ensure we're on the UI thread before calling navigation
        // TODO; why on UI thread?
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            if (DataContext is ViewModels.MainViewModel vm)
            {
                await vm.PlayNextEpisodeOrGoBack();
            }
        });
    }
    private void OnConfigurationToggleRequested(object? sender, EventArgs e)
    {
        Border? configLayer = this.FindControl<Border>("ConfigLayer");
        if (configLayer == null)
        {
            return;
        }

        configLayer.IsVisible = !configLayer.IsVisible;

        if (configLayer.IsVisible)
        {
            var app = (App)Application.Current!;
            IConfiguration config = app.Configuration;

            var items = config.AsEnumerable()
                .OrderBy(x => x.Key)
                .ToList();

            ItemsControl? itemsControl = this.FindControl<ItemsControl>("ConfigItemsControl");
            if (itemsControl != null)
            {
                itemsControl.ItemsSource = items;
            }
        }
    }
}
