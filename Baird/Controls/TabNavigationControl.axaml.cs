using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Baird.ViewModels;
using ReactiveUI;
using System;
using System.Linq;
using System.Reactive.Disposables;

namespace Baird.Controls
{
    public partial class TabNavigationControl : UserControl
    {
        private CompositeDisposable? _subscriptions;
        private bool _hasAutoFocused = false;

        public TabNavigationControl()
        {
            InitializeComponent();

            this.AttachedToVisualTree += OnAttachedToVisualTree;
            this.DetachedFromVisualTree += OnDetachedFromVisualTree;
            this.DataContextChanged += OnDataContextChanged;
        }

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            _hasAutoFocused = false;
            this.LayoutUpdated += OnLayoutUpdated;
            Console.WriteLine("[TabNav] Attached to visual tree, waiting for layout...");
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            this.LayoutUpdated -= OnLayoutUpdated;
            _subscriptions?.Dispose();
            _subscriptions = null;
        }

        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            if (!this.IsVisible) return;

            if (!_hasAutoFocused)
            {
                FocusSelectedTab();
            }

            // Ensure the selected tab is centered, e.g. after a resize
            CenterSelectedTab();
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            _subscriptions?.Dispose();
            _subscriptions = new CompositeDisposable();

            if (DataContext is TabNavigationViewModel vm)
            {
                vm.WhenAnyValue(x => x.SelectedTab)
                    .Subscribe(selectedTab =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            UpdateTabStyles();
                            CenterSelectedTab();
                        }, DispatcherPriority.Input);
                    })
                    .DisposeWith(_subscriptions);
            }
        }

        private void FocusSelectedTab()
        {
            if (DataContext is not TabNavigationViewModel vm) return;

            var itemsControl = this.FindControl<ItemsControl>("TabBar");
            if (itemsControl == null || itemsControl.ItemCount == 0) return;

            var selectedTab = vm.SelectedTab;
            int index = 0;
            if (selectedTab != null)
            {
                index = vm.Tabs.IndexOf(selectedTab);
                if (index < 0) index = 0;
            }

            var container = itemsControl.ContainerFromIndex(index);
            if (container is Visual visual)
            {
                var button = visual.FindDescendantOfType<Button>();
                if (button != null && button.IsEffectivelyVisible)
                {
                    button.Focus();
                    _hasAutoFocused = true;

                    Console.WriteLine($"[TabNav] Auto-focused tab button: {index}");
                    UpdateTabStyles();
                    CenterSelectedTab();
                }
            }
        }

        private void CenterSelectedTab()
        {
            if (DataContext is not TabNavigationViewModel vm || vm.SelectedTab == null)
                return;

            var itemsControl = this.FindControl<ItemsControl>("TabBar");
            var canvas = this.FindControl<Canvas>("CarouselContainer");

            if (itemsControl == null || canvas == null) return;

            // Find index of selected tab
            var index = vm.Tabs.IndexOf(vm.SelectedTab);
            if (index < 0) return;

            var container = itemsControl.ContainerFromIndex(index) as Control;
            if (container == null) return;

            // Calculate centers
            // container.Bounds.X is relative to the StackPanel (ItemsControl content)
            // We want to shift the ItemsControl (Canvas.Left) so that (container.X + container.Width/2) + Canvas.Left == Canvas.Width/2

            double containerCenter = container.Bounds.Position.X + (container.Bounds.Width / 2);
            double viewportCenter = canvas.Bounds.Width / 2;

            double targetLeft = viewportCenter - containerCenter;

            // Update Canvas.Left
            // Use SetValue to trigger the transition
            itemsControl.SetValue(Canvas.LeftProperty, targetLeft);
        }

        private void UpdateTabStyles()
        {
            if (DataContext is not TabNavigationViewModel vm || vm.SelectedTab == null)
                return;

            var selectedTab = vm.SelectedTab;
            var itemsControl = this.FindControl<ItemsControl>("TabBar");
            if (itemsControl == null) return;

            for (int i = 0; i < itemsControl.ItemCount; i++)
            {
                var container = itemsControl.ContainerFromIndex(i);
                if (container is Visual visual)
                {
                    var button = visual.FindDescendantOfType<Button>();
                    if (button != null)
                    {
                        var tabItem = itemsControl.Items.Cast<Baird.ViewModels.TabItem>().ElementAtOrDefault(i);
                        if (tabItem != null)
                        {
                            if (tabItem == selectedTab)
                            {
                                button.Classes.Add("selected");
                            }
                            else
                            {
                                button.Classes.Remove("selected");
                            }
                        }
                    }
                }
            }
        }

        private void OnTabButtonGotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (DataContext is TabNavigationViewModel vm && sender is Control control && control.DataContext is Baird.ViewModels.TabItem tabItem)
            {
                vm.SelectedTab = tabItem;
                // Once a tab is focused, allow moving to siblings
                SetSiblingsFocusable(true);
            }
        }

        private void OnContentGotFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // When focus enters the content area, restrict tab focus to ONLY the selected tab.
            // This ensures navigating UP from content always hits the selected tab.
            SetSiblingsFocusable(false);
        }

        private void OnTabBarPointerEntered(object? sender, Avalonia.Input.PointerEventArgs e)
        {
            // If mouse enters the tab bar, allow clicking any tab
            SetSiblingsFocusable(true);
        }

        private void SetSiblingsFocusable(bool allowAll)
        {
            if (DataContext is not TabNavigationViewModel vm) return;
            var selectedTab = vm.SelectedTab;

            var itemsControl = this.FindControl<ItemsControl>("TabBar");
            if (itemsControl == null) return;

            for (int i = 0; i < itemsControl.ItemCount; i++)
            {
                var container = itemsControl.ContainerFromIndex(i);
                if (container is Visual visual)
                {
                    var button = visual.FindDescendantOfType<Button>();
                    if (button != null)
                    {
                        var tabItem = itemsControl.Items.Cast<Baird.ViewModels.TabItem>().ElementAtOrDefault(i);
                        if (tabItem != null)
                        {
                            bool isSelected = (tabItem == selectedTab);
                            // If allowAll is true, everyone is focusable.
                            // If allowAll is false, only the selected one is focusable.
                            button.Focusable = allowAll || isSelected;
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
