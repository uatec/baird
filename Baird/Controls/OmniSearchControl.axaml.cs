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
        public System.Windows.Input.ICommand MoveFocusDownCommand { get; }

        public OmniSearchControl()
        {
            InitializeComponent();
            MoveFocusDownCommand = ReactiveUI.ReactiveCommand.Create(MoveFocusDown);

            this.AttachedToVisualTree += (s, e) =>
            {
                var box = this.FindControl<TextBox>("SearchBox");
                if (box != null)
                {
                    box.GotFocus += (sender, args) => UpdateFocusState(true);
                    box.LostFocus += (sender, args) => UpdateFocusState(false);
                }
            };
        }

        private void MoveFocusDown()
        {
            // Try Suggestions First
            var suggestions = this.FindControl<ItemsControl>("SuggestionsList");
            if (suggestions != null && suggestions.ItemCount > 0 && suggestions.IsVisible)
            {
                var container = suggestions.ContainerFromIndex(0);
                if (container is Visual visual)
                {
                    var button = visual.FindDescendantOfType<Button>();
                    if (button != null && button.IsEffectivelyVisible)
                    {
                        button.Focus();
                        return;
                    }
                }
            }

            // Fallback to Results
            FocusResults();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is ViewModels.OmniSearchViewModel vm)
            {
                vm.SearchBoxFocusRequested += (s, args) =>
                {
                    Dispatcher.UIThread.Post(FocusSearchBox, DispatcherPriority.Input);
                };
            }
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
