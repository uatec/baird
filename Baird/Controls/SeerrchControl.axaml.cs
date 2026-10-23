using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;

namespace Baird.Controls
{
    public partial class SeerrchControl : UserControl
    {
        // Must match the UniformGrid Columns of ResultsList in SeerrchControl.axaml.
        private const int ResultColumns = 4;

        private SearchPaneNavigation? _navigation;

        public SeerrchControl()
        {
            InitializeComponent();

            // Tunnel so the pane hand-off runs before XY focus picks a neighbour.
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

            this.AttachedToVisualTree += (s, e) =>
            {
                // Don't auto-focus - let user press Down from tab navigation
                var keyboard = this.FindControl<VirtualKeyboardControl>("VirtualKeyboard")!;
                keyboard.KeyPressed += key => (DataContext as ViewModels.ISearchTextInput)?.AppendText(key.ToLowerInvariant());
                keyboard.BackspacePressed += () => (DataContext as ViewModels.ISearchTextInput)?.Backspace();
                keyboard.EnterPressed += () => Navigation.FocusFirstResult();
            };
        }

        private SearchPaneNavigation Navigation => _navigation ??= new SearchPaneNavigation(
            this.FindControl<VirtualKeyboardControl>("VirtualKeyboard")!,
            this.FindControl<ItemsControl>("ResultsList")!,
            ResultColumns,
            suggestions: null,
            FocusTabBar);

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            if (Navigation.Handle(e.Key, TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()))
                e.Handled = true;
        }

        private void FocusTabBar()
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
                        }
                    }
                }
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
