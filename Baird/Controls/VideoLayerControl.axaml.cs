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

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
        
        // Expose Player for external control if needed, or binding
        public VideoPlayer? GetPlayer()
        {
            return this.FindControl<VideoPlayer>("Player");
        }
    }
}
