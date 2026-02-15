using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Baird.Controls
{
    public partial class WatchlistControl : UserControl
    {
        public WatchlistControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
