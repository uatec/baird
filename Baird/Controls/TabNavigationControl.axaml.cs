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
        private bool _hasBeenShownBefore = false;

        public TabNavigationControl()
        {
            InitializeComponent();

            // Focus handling when control becomes visible
            this.GetObservable(IsVisibleProperty).Subscribe(visible =>
            {
                if (visible)
                {
                    Dispatcher.UIThread.Post(() => 
                    {
                        if (!_hasBeenShownBefore)
                        {
                            // First time showing: focus on first tab button
                            FocusFirstTabButton();
                            _hasBeenShownBefore = true;
                        }
                        else
                        {
                            // Returning to this control: maintain state, just update tab styles
                            UpdateTabStyles();
                        }
                    }, DispatcherPriority.Input);
                }
            });

            this.AttachedToVisualTree += (s, e) =>
            {
                if (IsVisible && !_hasBeenShownBefore)
                {
                    Dispatcher.UIThread.Post(FocusFirstTabButton, DispatcherPriority.Input);
                    _hasBeenShownBefore = true;
                }
            };

            this.DetachedFromVisualTree += (s, e) =>
            {
                _subscriptions?.Dispose();
                _subscriptions = null;
            };

            // Listen for tab changes to update styles
            this.DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object? sender, EventArgs e)
        {
            _subscriptions?.Dispose();
            _subscriptions = new CompositeDisposable();

            if (DataContext is TabNavigationViewModel vm)
            {
                var subscription = vm.WhenAnyValue(x => x.SelectedTab)
                    .Subscribe(selectedTab =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            UpdateTabStyles();
                        }, DispatcherPriority.Input);
                    });
                _subscriptions.Add(subscription);
            }
        }

        private void FocusFirstTabButton()
        {
            var itemsControl = this.FindControl<ItemsControl>("TabBar");
            if (itemsControl == null || itemsControl.ItemCount == 0) return;

            var container = itemsControl.ContainerFromIndex(0);
            if (container is Visual visual)
            {
                var button = visual.FindDescendantOfType<Button>();
                button?.Focus();
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
                        var tabItem = itemsControl.Items.Cast<ViewModels.TabItem>().ElementAtOrDefault(i);
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

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
