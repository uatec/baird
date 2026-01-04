using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Interactivity;
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
            
            var list = this.FindControl<ListBox>("ResultsList");
            if (list != null)
            {
                // Intercept KeyDown even if handled by sub-elements
                list.AddHandler(InputElement.KeyDownEvent, (s, e) => 
                {
                    if (e.Key == global::Avalonia.Input.Key.Enter || e.Key == global::Avalonia.Input.Key.Return)
                    {
                        var lb = s as ListBox;
                        if (lb?.SelectedItem is MediaItem item)
                        {
                            ItemChosen?.Invoke(this, item);
                            e.Handled = true;
                        }
                    }
                    else if (e.Key == global::Avalonia.Input.Key.Up)
                    {
                        var lb = s as ListBox;
                        if (lb != null && lb.SelectedIndex == 0)
                        {
                            Console.WriteLine("ResultsList: Top reached, clearing selection and returning focus to SearchBox");
                            lb.SelectedIndex = -1;
                            FocusSearchBox();
                            e.Handled = true;
                        }
                    }
                }, RoutingStrategies.Bubble, true);
            }

            var box = this.FindControl<TextBox>("SearchBox");
            if (box != null)
            {
                // CRITICAL: We MUST use handledEventsToo: true because TextBox handles Down key internally
                box.AddHandler(InputElement.KeyDownEvent, (s, e) =>
                {
                    if (e.Key == global::Avalonia.Input.Key.Down)
                    {
                        if (DataContext is Baird.ViewModels.OmniSearchViewModel vm && !vm.IsKeyboardVisible)
                        {
                            var resultList = this.FindControl<ListBox>("ResultsList");
                            if (resultList != null && resultList.ItemCount > 0)
                            {
                                Console.WriteLine("SearchBox: Intercepting Down key to focus results");
                                
                                // Ensure something is selected
                                if (resultList.SelectedIndex < 0)
                                    resultList.SelectedIndex = 0;
                                
                                // Hand off focus to the Results List
                                // We use a small delay to ensure the TextBox has finished its cycle
                                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                                {
                                    // Try to focus the item container for maximum reliability
                                    var container = resultList.ContainerFromIndex(resultList.SelectedIndex);
                                    if (container != null)
                                    {
                                        container.Focus();
                                    }
                                    else
                                    {
                                        resultList.Focus();
                                    }
                                }, Avalonia.Threading.DispatcherPriority.Input);

                                e.Handled = true;
                            }
                        }
                    }
                }, RoutingStrategies.Bubble, true);
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
        
        public void FocusResults()
        {
            var list = this.FindControl<ListBox>("ResultsList");
            list?.Focus();
        }

        public void FocusSearchBox()
        {
             var box = this.FindControl<TextBox>("SearchBox"); 
             if (box != null)
             {
                 box.Focus();
                 // Ensure cursor is at the end so appended digits are before the cursor
                 box.CaretIndex = box.Text?.Length ?? 0;
             }
        }
    }
}
