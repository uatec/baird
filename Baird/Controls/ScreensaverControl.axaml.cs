using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Baird.ViewModels;

namespace Baird.Controls;

public partial class ScreensaverControl : UserControl
{
    public ScreensaverControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Auto-play when attached
        VideoPlayer? player = this.FindControl<VideoPlayer>("ScreensaverPlayer");
        if (player != null && DataContext is ScreensaverViewModel vm && vm.CurrentAsset?.VideoUrl != null)
        {
            Console.WriteLine($"[ScreensaverControl] Attached. Playing {vm.CurrentAsset.VideoUrl}");
            player.Play(vm.CurrentAsset.VideoUrl);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // Ensure stop when detached
        VideoPlayer? player = this.FindControl<VideoPlayer>("ScreensaverPlayer");
        if (player != null)
        {
            Console.WriteLine("[ScreensaverControl] Detached. Stopping player.");
            player.Stop();
        }
        base.OnDetachedFromVisualTree(e);
    }
}
