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
            var keyboard = this.FindControl<VirtualKeyboardControl>("VirtualKeyboard");
            
            if (keyboard != null && box != null)
            {
                keyboard.KeyPressed += (key) =>
                {
                   if (box.Text == null) box.Text = "";
                   box.Text += key; 
                   box.CaretIndex = box.Text.Length;
                };

                keyboard.BackspacePressed += () =>
                {
                    if (!string.IsNullOrEmpty(box.Text))
                    {
                        box.Text = box.Text.Substring(0, box.Text.Length - 1);
                        box.CaretIndex = box.Text.Length;
                    }
                };
                
                keyboard.EnterPressed += () =>
                {
                    if (DataContext is Baird.ViewModels.OmniSearchViewModel vm)
                    {
                        vm.IsKeyboardVisible = false;
                        Dispatcher.UIThread.Post(FocusResults);
                    }
                };
            }

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
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
        
        private void OnResultClicked(object? sender, RoutedEventArgs e)
        {
            if (sender is Control c && c.DataContext is MediaItem item)
            {
                 ItemChosen?.Invoke(this, item);
            }
        }
        
        public async void FocusResults()
        {
            var list = this.FindControl<ItemsControl>("ResultsList");
            if (list == null) return;
            
            // Retry loop to find container
            for(int i=0; i<10; i++) 
            {
                if (list.ItemCount > 0)
                {
                    var container = list.ContainerFromIndex(0);
                    if (container == null) 
                    {
                        list.UpdateLayout();
                        container = list.ContainerFromIndex(0);
                    }
                    
                    if (container is Visual v)
                    {
                        var btn = v.FindDescendantOfType<Button>();
                        if (btn != null)
                        {
                            btn.Focus();
                            return;
                        }
                    }
                }
                await System.Threading.Tasks.Task.Delay(50);
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
