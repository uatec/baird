using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;

namespace Baird.Controls
{
    public partial class SeerrchControl : UserControl
    {
        public System.Windows.Input.ICommand MoveFocusDownCommand { get; }

        public SeerrchControl()
        {
            InitializeComponent();
            MoveFocusDownCommand = ReactiveUI.ReactiveCommand.Create(MoveFocusDown);
        }

        private void MoveFocusDown()
        {
            FocusResults();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is ViewModels.SeerrchViewModel vm)
            {
                vm.SearchBoxFocusRequested += (s, args) =>
                {
                    Dispatcher.UIThread.Post(FocusSearchBox, DispatcherPriority.Input);
                };
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public void FocusResults()
        {
            var itemsControl = this.FindControl<ItemsControl>("ResultsList");
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

        public void FocusSearchBox()
        {
            var box = this.FindControl<TextBox>("SearchBox");
            if (box != null)
            {
                box.Focus();
                box.SelectAll();
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            
            // Auto-focus search box when control is shown
            Dispatcher.UIThread.Post(() =>
            {
                FocusSearchBox();
            }, DispatcherPriority.Loaded);
        }
    }
}
