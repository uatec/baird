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


        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
        }

        public OmniSearchControl()
        {
            InitializeComponent();
            
            var list = this.FindControl<ListBox>("ResultsList");
            if (list != null)
            {
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

            this.GetObservable(IsVisibleProperty).Subscribe(visible => 
            {
                if (visible)
                {
                    Dispatcher.UIThread.Post(FocusSearchBox, DispatcherPriority.Input);
                }
            });

            this.AttachedToVisualTree += (s, e) => 
            {
                if (IsVisible)
                {
                    Dispatcher.UIThread.Post(FocusSearchBox, DispatcherPriority.Input);
                }

                var box = this.FindControl<TextBox>("SearchBox");
                if (box != null)
                {
                    box.GotFocus += (sender, args) => UpdateFocusState(true);
                    box.LostFocus += (sender, args) => UpdateFocusState(false);
                }
            };
        }

        private void UpdateFocusState(bool isFocused)
        {
            if (DataContext is ViewModels.OmniSearchViewModel vm)
            {
                vm.IsSearchFieldFocused = isFocused;
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
