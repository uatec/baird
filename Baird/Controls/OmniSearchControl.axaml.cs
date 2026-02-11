using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Baird.Services;
using System;

namespace Baird.Controls
{
    public partial class OmniSearchControl : UserControl
    {
        public OmniSearchControl()
        {
            InitializeComponent();

            this.GetObservable(IsVisibleProperty).Subscribe(visible =>
            {
                if (visible)
                {
                    Dispatcher.UIThread.Post(FocusSearchBox, DispatcherPriority.Input);
                }
            });

            this.AttachedToVisualTree += (s, e) =>
            {
                if (IsVisible)
                {
                    Dispatcher.UIThread.Post(FocusSearchBox, DispatcherPriority.Input);
                }

                var box = this.FindControl<TextBox>("SearchBox");
                if (box != null)
                {
                    box.GotFocus += (sender, args) => UpdateFocusState(true);
                    box.LostFocus += (sender, args) => UpdateFocusState(false);
                }
            };
        }

        private void UpdateFocusState(bool isFocused)
        {
            if (DataContext is ViewModels.OmniSearchViewModel vm)
            {
                vm.IsSearchFieldFocused = isFocused;
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
                box.CaretIndex = box.Text?.Length ?? 0;
            }
        }
    }
}
