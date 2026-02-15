using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Baird.ViewModels;
using ReactiveUI;

namespace Baird.Controls;

public partial class TabNavigationControl : UserControl, IDisposable
{
    private CompositeDisposable? _subscriptions;
    private bool _hasAutoFocused = false;

    public TabNavigationControl()
    {
        InitializeComponent();

        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _hasAutoFocused = false;
        LayoutUpdated += OnLayoutUpdated;
        Console.WriteLine("[TabNav] Attached to visual tree, waiting for layout...");
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        _subscriptions?.Dispose();
        _subscriptions = null;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_hasAutoFocused || !IsVisible)
        {
            return;
        }

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
        ItemsControl? itemsControl = this.FindControl<ItemsControl>("TabBar");
        if (itemsControl == null || itemsControl.ItemCount == 0)
        {
            return;
        }

        Control? container = itemsControl.ContainerFromIndex(0);
        if (container is Visual visual)
        {
            Button? button = visual.FindDescendantOfType<Button>();
            if (button != null && button.IsEffectivelyVisible)
            {
                button.Focus();
                _hasAutoFocused = true;
                LayoutUpdated -= OnLayoutUpdated; // Stop listening once focused

                Console.WriteLine("[TabNav] Auto-focused first tab button.");
                UpdateTabStyles();
            }
        }
    }

    private void UpdateTabStyles()
    {
        if (DataContext is not TabNavigationViewModel vm || vm.SelectedTab == null)
        {
            return;
        }

        ViewModels.TabItem selectedTab = vm.SelectedTab;
        ItemsControl? itemsControl = this.FindControl<ItemsControl>("TabBar");
        if (itemsControl == null)
        {
            return;
        }

        for (int i = 0; i < itemsControl.ItemCount; i++)
        {
            Control? container = itemsControl.ContainerFromIndex(i);
            if (container is Visual visual)
            {
                Button? button = visual.FindDescendantOfType<Button>();
                if (button != null)
                {
                    ViewModels.TabItem? tabItem = itemsControl.Items.Cast<Baird.ViewModels.TabItem>().ElementAtOrDefault(i);
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

    public void Dispose()
    {
        throw new NotImplementedException();
    }
}
