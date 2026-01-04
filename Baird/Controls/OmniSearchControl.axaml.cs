using Avalonia.Controls;
using Avalonia.Markup.Xaml;
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
                list.KeyDown += (s, e) => 
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
                };
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
