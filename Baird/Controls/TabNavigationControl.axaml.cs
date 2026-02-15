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
            if (_hasAutoFocused || !this.IsVisible) return;

            AttemptFocusFirstTab();
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
                        Dispatcher.UIThread.Post(() => UpdateTabStyles(), DispatcherPriority.Input);
                    })
                    .DisposeWith(_subscriptions);
            }
        }

        private void AttemptFocusFirstTab()
        {
            var itemsControl = this.FindControl<ItemsControl>("TabBar");
            if (itemsControl == null || itemsControl.ItemCount == 0) return;

            var container = itemsControl.ContainerFromIndex(0);
            if (container is Visual visual)
            {
                var button = visual.FindDescendantOfType<Button>();
                if (button != null && button.IsEffectivelyVisible)
                {
                    button.Focus();
                    _hasAutoFocused = true;
                    this.LayoutUpdated -= OnLayoutUpdated; // Stop listening once focused

                    Console.WriteLine("[TabNav] Auto-focused first tab button.");
                    UpdateTabStyles();
                }
            }
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
                //Console.WriteLine($"[TabNav] Auto-selected tab on focus: {tabItem.Title}");
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
