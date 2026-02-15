using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace Baird.Controls;

public partial class HistoryControl : UserControl
{
    public HistoryControl()
    {
        InitializeComponent();
    }

    public void FocusHistoryList()
    {
        ItemsControl? itemsControl = this.FindControl<ItemsControl>("HistoryList");
        if (itemsControl == null)
        {
            return;
        }

        // Try to focus the first button in the grid
        if (itemsControl.ItemCount > 0)
        {
            Control? container = itemsControl.ContainerFromIndex(0);
            if (container is Visual visual)
            {
                Button? button = visual.FindDescendantOfType<Button>();
                button?.Focus();
            }
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
