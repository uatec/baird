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

        public TabNavigationControl()
        {
            InitializeComponent();

            // Focus first tab when visible
            this.GetObservable(IsVisibleProperty).Subscribe(visible =>
            {
                if (visible)
                {
                    Dispatcher.UIThread.Post(FocusContent, DispatcherPriority.Input);
                }
            });

            this.AttachedToVisualTree += (s, e) =>
            {
                if (IsVisible)
                {
                    Dispatcher.UIThread.Post(FocusContent, DispatcherPriority.Input);
                }
            };

            this.DetachedFromVisualTree += (s, e) =>
            {
                _subscriptions?.Dispose();
                _subscriptions = null;
            };

            // Listen for tab changes to focus content
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
                            UpdateTabStyles(selectedTab);
                            FocusContent();
                        }, DispatcherPriority.Input);
                    });
                _subscriptions.Add(subscription);
            }
        }

        private void UpdateTabStyles(ViewModels.TabItem? selectedTab)
        {
            if (selectedTab == null) return;

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

        private void FocusContent()
        {
            if (DataContext is not TabNavigationViewModel vm || vm.SelectedTab == null)
                return;

            // Focus the appropriate control based on the selected tab's content
            var content = vm.SelectedTab.Content;
            
            if (content is HistoryViewModel)
            {
                // Find and focus the HistoryControl
                var historyControl = this.FindDescendantOfType<HistoryControl>();
                historyControl?.FocusHistoryList();
            }
            else if (content is OmniSearchViewModel)
            {
                // Find and focus the OmniSearchControl
                var searchControl = this.FindDescendantOfType<OmniSearchControl>();
                searchControl?.FocusSearchBox();
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
