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
        public event EventHandler<MediaItem>? ItemChosen;

        public OmniSearchControl()
        {
            InitializeComponent();
            
            var box = this.FindControl<TextBox>("SearchBox");


            if (box != null)
            {
                box.KeyDown += (s, e) => 
                {
                    if (e.Key == global::Avalonia.Input.Key.Enter || e.Key == global::Avalonia.Input.Key.Return)
                    {
                        if (DataContext is Baird.ViewModels.OmniSearchViewModel vm && vm.SelectedItem != null)
                        {
                            ItemChosen?.Invoke(this, vm.SelectedItem);
                            e.Handled = true;
                        }
                    }
                };
            }
            
            var list = this.FindControl<ListBox>("ResultsList");
            if (list != null)
            {
                list.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter || e.Key == Key.Return)
                    {
                        if (s is ListBox lb && lb.SelectedItem is MediaItem item)
                        {
                            ItemChosen?.Invoke(this, item);
                            e.Handled = true;
                        }
                    }
                };

                list.GetObservable(ListBox.BoundsProperty).Subscribe(bounds => 
                {
                    var width = bounds.Width;
                    if (width > 0)
                    {
                        // 300 is the ItemWidth defined in XAML
                        var columns = Math.Floor(width / 300);
                        // Prevent 0 width if very small, though unlikely
                        if (columns < 1) columns = 1; 
                        
                        CalculatedWidth = columns * 300;
                    }
                });
            }
        }

        public static readonly StyledProperty<double> CalculatedWidthProperty =
            AvaloniaProperty.Register<OmniSearchControl, double>(nameof(CalculatedWidth), 300);

        public double CalculatedWidth
        {
            get => GetValue(CalculatedWidthProperty);
            set => SetValue(CalculatedWidthProperty, value);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
        
        private void OnResultTapped(object? sender, TappedEventArgs e)
        {
            if (sender is ListBox list && e.Source is Visual v)
            {
               // Find the container (ListBoxItem) from the visual source
               var container = v.FindAncestorOfType<ListBoxItem>();
               if (container != null && container.DataContext is MediaItem item)
               {
                   ItemChosen?.Invoke(this, item);
               }
            }
        }
        
        public void FocusResults()
        {
            var list = this.FindControl<ListBox>("ResultsList");
            if (list == null) return;
            
            list.Focus();
            
            // If we have items, ensure one is selected to show the highlight
            if (list.SelectedIndex < 0 && list.ItemCount > 0)
            {
                list.SelectedIndex = 0;
            }
        }

        public void FocusSearchBox()
        {
             var box = this.FindControl<TextBox>("SearchBox"); 
             if (box != null)
             {
                 box.Focus();
                 box.CaretIndex = box.Text?.Length ?? 0;
             }
        }
    }
}
