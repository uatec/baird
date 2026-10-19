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
        public System.Windows.Input.ICommand MoveFocusUpCommand { get; }

        public SeerrchControl()
        {
            InitializeComponent();
            MoveFocusDownCommand = ReactiveUI.ReactiveCommand.Create(MoveFocusDown);
            MoveFocusUpCommand = ReactiveUI.ReactiveCommand.Create(MoveFocusUp);
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
            var itemsControl = this.FindControl<ItemsControl>("ResultsList");
            if (itemsControl != null && itemsControl.ItemCount > 0)
            {
                FocusResults();
                return;
            }

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
            // Don't auto-focus - let user press Down from tab navigation

            var box = this.FindControl<TextBox>("SearchBox");
            if (box != null)
            {
                box.GotFocus += (sender, args) =>
                {
                    Dispatcher.UIThread.Post(() => box.SelectAll(), DispatcherPriority.Input);
                };
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
        }
    }
}
