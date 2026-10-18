using Avalonia;
using Avalonia.Controls;
using Baird.ViewModels;

namespace Baird.Controls
{
    public partial class AvDebugControl : UserControl
    {
        public AvDebugControl()
        {
            InitializeComponent();
        }

        // Sample only while the page is on screen (unless journal logging keeps it running).
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (DataContext is AvDebugViewModel vm) vm.IsPageVisible = true;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (DataContext is AvDebugViewModel vm) vm.IsPageVisible = false;
            base.OnDetachedFromVisualTree(e);
        }
    }
}
