using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;

namespace Baird.Controls
{
    public partial class HistoryControl : UserControl
    {
        public HistoryControl()
        {
            InitializeComponent();
            
            // Re-focus whenever visibility changes to true
            this.GetObservable(IsVisibleProperty).Subscribe(visible => 
            {
                if (visible)
                {
                    Dispatcher.UIThread.Post(FocusHistoryList, DispatcherPriority.Input);
                }
            });

            // Focus on attach if visible
            this.AttachedToVisualTree += (s, e) => 
            {
                if (IsVisible)
                {
                    Dispatcher.UIThread.Post(FocusHistoryList, DispatcherPriority.Input);
                }
            };
        }

        public void FocusHistoryList()
        {
            var list = this.FindControl<ListBox>("HistoryList");
            if (list == null) return;
            
            list.Focus();
            
            // If we have items, ensure one is selected to show the highlight
            if (list.SelectedIndex < 0 && list.ItemCount > 0)
            {
                list.SelectedIndex = 0;
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
