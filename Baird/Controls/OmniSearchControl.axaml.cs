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
    public partial class OmniSearchControl : UserControl
    {
        // Must match the UniformGrid Columns of ResultsList in OmniSearchControl.axaml.
        private const int ResultColumns = 4;

        private SearchPaneNavigation? _navigation;

        public OmniSearchControl()
        {
            InitializeComponent();

            // Tunnel so the pane hand-off runs before XY focus picks a neighbour.
            AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

            this.AttachedToVisualTree += (s, e) =>
            {
                var keyboard = this.FindControl<VirtualKeyboardControl>("VirtualKeyboard")!;
                keyboard.KeyPressed += key => (DataContext as ViewModels.ISearchTextInput)?.AppendText(key.ToLowerInvariant());
                keyboard.BackspacePressed += () => (DataContext as ViewModels.ISearchTextInput)?.Backspace();
                keyboard.EnterPressed += () => Navigation.FocusFirstResult();

                if (DataContext is ViewModels.OmniSearchViewModel vm && vm.FocusKeyboardOnLoad)
                {
                    // Delay slightly to ensure layout is done and tab nav focus settled
                    Dispatcher.UIThread.Post(() =>
                    {
                        Navigation.FocusKeyboard();
                        vm.FocusKeyboardOnLoad = false;
                    }, DispatcherPriority.Input);
                }
            };
        }

        private SearchPaneNavigation Navigation => _navigation ??= new SearchPaneNavigation(
            this.FindControl<VirtualKeyboardControl>("VirtualKeyboard")!,
            this.FindControl<ItemsControl>("ResultsList")!,
            ResultColumns,
            this.FindControl<ItemsControl>("SuggestionsList"),
            FocusTabBar);

        private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
        {
            // Remote number keys build a channel-number query without leaving the results.
            if (TryGetDigit(e.Key) is { } digit)
            {
                (DataContext as ViewModels.ISearchTextInput)?.AppendText(digit);
                e.Handled = true;
                return;
            }

            if (Navigation.Handle(e.Key, TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement()))
                e.Handled = true;
        }

        private static string? TryGetDigit(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9) return ((int)key - (int)Key.D0).ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return ((int)key - (int)Key.NumPad0).ToString();
            return null;
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

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is ViewModels.OmniSearchViewModel vm)
            {
                vm.KeyboardFocusRequested += (s, args) =>
                    Dispatcher.UIThread.Post(() => Navigation.FocusKeyboard(), DispatcherPriority.Input);

                if (this.IsEffectivelyVisible && vm.FocusKeyboardOnLoad)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        Navigation.FocusKeyboard();
                        vm.FocusKeyboardOnLoad = false;
                    }, DispatcherPriority.Input);
                }
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
