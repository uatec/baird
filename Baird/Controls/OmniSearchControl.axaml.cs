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
        public System.Windows.Input.ICommand MoveFocusUpCommand { get; }

        private bool _suppressNextSelectAll = false;

        public OmniSearchControl()
        {
            InitializeComponent();
            MoveFocusDownCommand = ReactiveUI.ReactiveCommand.Create(MoveFocusDown);
            MoveFocusUpCommand = ReactiveUI.ReactiveCommand.Create(MoveFocusUp);

            this.AttachedToVisualTree += (s, e) =>
            {
                var box = this.FindControl<TextBox>("SearchBox");
                if (box != null)
                {
                    box.GotFocus += (sender, args) =>
                    {
                        UpdateFocusState(true);
                        if (!_suppressNextSelectAll)
                            Dispatcher.UIThread.Post(() => box.SelectAll(), DispatcherPriority.Input);
                        else
                            Dispatcher.UIThread.Post(() => { var len = box.Text?.Length ?? 0; box.SelectionStart = len; box.SelectionEnd = len; }, DispatcherPriority.Input);
                        _suppressNextSelectAll = false;
                    };
                    box.LostFocus += (sender, args) => UpdateFocusState(false);
                }

                var keyboard = this.FindControl<VirtualKeyboardControl>("VirtualKeyboard");
                if (keyboard != null)
                {
                    keyboard.KeyPressed += key =>
                    {
                        if (box != null)
                        {
                            var text = box.Text ?? string.Empty;
                            box.Text = text + key.ToLowerInvariant();
                            box.CaretIndex = box.Text.Length;
                        }
                    };
                    keyboard.BackspacePressed += () =>
                    {
                        if (box != null && !string.IsNullOrEmpty(box.Text))
                        {
                            box.Text = box.Text.Substring(0, box.Text.Length - 1);
                            box.CaretIndex = box.Text.Length;
                        }
                    };
                    keyboard.EnterPressed += () =>
                    {
                        FocusResults();
                    };
                }

                if (DataContext is ViewModels.OmniSearchViewModel vm && vm.FocusSearchBoxOnLoad)
                {
                    // Delay slightly to ensure layout is done and tab nav focus settled
                    Dispatcher.UIThread.Post(() =>
                    {
                        FocusSearchBox();
                        vm.FocusSearchBoxOnLoad = false;
                    }, DispatcherPriority.Input);
                }
            };
        }

        private void MoveFocusUp()
        {
            var tabNav = this.FindAncestorOfType<TabNavigationControl>();
            if (tabNav != null)
            {
                var itemsControl = tabNav.FindControl<ItemsControl>("TabBar");
                if (itemsControl != null && tabNav.DataContext is ViewModels.TabNavigationViewModel vm && vm.SelectedTab != null)
                {
                    var index = vm.Tabs.IndexOf(vm.SelectedTab);
                    if (index >= 0)
                    {
                        var container = itemsControl.ContainerFromIndex(index);
                        if (container is Visual visual)
                        {
                            var button = visual.FindDescendantOfType<Button>();
                            button?.Focus();
                            return;
                        }
                    }
                }
            }
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

            // If we have results, focus results
            var itemsControl = this.FindControl<ItemsControl>("ResultsList");
            if (itemsControl != null && itemsControl.ItemCount > 0)
            {
                FocusResults();
                return;
            }

            // Otherwise, focus the virtual keyboard (KeyF)
            var keyboard = this.FindControl<VirtualKeyboardControl>("VirtualKeyboard");
            if (keyboard != null && keyboard.IsEffectivelyVisible)
            {
                var keyF = keyboard.FindControl<Button>("KeyF");
                if (keyF != null && keyF.IsEffectivelyVisible)
                {
                    keyF.Focus();
                    return;
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
                vm.SearchBoxFocusRequested += (s, selectAll) =>
                {
                    // Set the suppress flag synchronously NOW, before any GotFocus event
                    // can fire during tab-navigation to the search box.
                    _suppressNextSelectAll = !selectAll;
                    Dispatcher.UIThread.Post(FocusSearchBox, DispatcherPriority.Input);
                };

                if (this.IsEffectivelyVisible && vm.FocusSearchBoxOnLoad)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        FocusSearchBox();
                        vm.FocusSearchBoxOnLoad = false;
                    }, DispatcherPriority.Input);
                }
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
            if (box == null) return;

            // Read the intent directly from the ViewModel — durable regardless of
            // whether the control was attached when RequestSearchBoxFocus was called.
            bool selectAll = (DataContext is ViewModels.OmniSearchViewModel vm)
                ? vm.SelectAllOnNextFocus
                : true;
            _suppressNextSelectAll = !selectAll;
            box.Focus();
        }
    }
}
