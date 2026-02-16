using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Baird.ViewModels;
using System;

namespace Baird.Controls
{
    public partial class ScreensaverControl : UserControl
    {
        private VideoPlayer? _player;

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
            _player = this.FindControl<VideoPlayer>("ScreensaverPlayer");
            if (_player != null)
            {
                // Subscribe to StreamEnded event
                _player.StreamEnded += OnScreensaverEnded;

                if (DataContext is ScreensaverViewModel vm && vm.CurrentAsset?.VideoUrl != null)
                {
                    Console.WriteLine($"[ScreensaverControl] Attached. Playing {vm.CurrentAsset.VideoUrl}");
                    _player.Play(vm.CurrentAsset.VideoUrl);
                }
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            // Unsubscribe and ensure stop when detached
            if (_player != null)
            {
                _player.StreamEnded -= OnScreensaverEnded;
                Console.WriteLine("[ScreensaverControl] Detached. Stopping player.");
                _player.Stop();
            }
            base.OnDetachedFromVisualTree(e);
        }

        private void OnScreensaverEnded(object? sender, EventArgs e)
        {
            Console.WriteLine("[ScreensaverControl] Screensaver video ended. Playing next random screensaver.");

            if (DataContext is ScreensaverViewModel vm)
            {
                vm.PlayNext();

                // Play the new video
                if (_player != null && vm.CurrentAsset?.VideoUrl != null)
                {
                    _player.Play(vm.CurrentAsset.VideoUrl);
                }
            }
        }
    }
}
