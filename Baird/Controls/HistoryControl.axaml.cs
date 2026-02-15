using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;

namespace Baird.Controls
{
    public partial class HistoryControl : UserControl
    {
        public HistoryControl()
        {
            InitializeComponent();
        }

        public void FocusHistoryList()
        {
            var itemsControl = this.FindControl<ItemsControl>("HistoryList");
            if (itemsControl == null) return;

            // Try to focus the first button in the grid
            if (itemsControl.ItemCount > 0)
            {
                var container = itemsControl.ContainerFromIndex(0);
                if (container is Visual visual)
                {
                    var button = visual.FindDescendantOfType<Button>();
                    button?.Focus();
                }
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
