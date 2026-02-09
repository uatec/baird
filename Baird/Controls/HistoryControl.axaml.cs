using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Baird.Controls
{
    public partial class HistoryControl : UserControl
    {
        public HistoryControl()
        {
            InitializeComponent();
            this.AttachedToVisualTree += (s, e) => 
            {
                var list = this.FindControl<ListBox>("HistoryList");
                list?.Focus();
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
