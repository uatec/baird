using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Baird.Controls
{
    public partial class MediaTileControl : UserControl
    {
        public MediaTileControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
