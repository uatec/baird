using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
            var box = this.FindControl<TextBox>("SearchBox");

            if (list != null)
            {
                // Handle activation
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
                        if (list.SelectedIndex == 0)
                        {
                            e.Handled = true;
                            Dispatcher.UIThread.Post(() => box?.Focus(), DispatcherPriority.Input);
                        }
                    }
                }, RoutingStrategies.Tunnel, true);
            }

            if (box != null)
            {
                // Clear selection when we move back up to the search box
                box.GotFocus += (s, e) => 
                {
                    if (list != null)
                    {
                        list.SelectedIndex = -1;
                    }
                };

                // Manual Down key handling to bridge to the list
                box.AddHandler(InputElement.KeyDownEvent, (s, e) => 
                {
                    if (e.Key == global::Avalonia.Input.Key.Down)
                    {
                        Console.WriteLine("Down key pressed in search box");
                        if (list != null && list.ItemCount > 0)
                        {
                            if (list.SelectedIndex < 0) list.SelectedIndex = 0;
                            e.Handled = true;
                            Dispatcher.UIThread.Post(() => 
                            {
                                // Focus the actual ListBoxItem container, not just the ListBox
                                var container = list.ContainerFromIndex(list.SelectedIndex);
                                if (container is ListBoxItem item)
                                {
                                    item.Focus();
                                }
                                else
                                {
                                    list.Focus();
                                }
                            }, DispatcherPriority.Input);
                        }
                    }
                }, RoutingStrategies.Tunnel, true);
            }
            var keyboard = this.FindControl<VirtualKeyboardControl>("VirtualKeyboard");
            
            bool navigatingToResults = false;

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
                        navigatingToResults = true;
                        vm.IsKeyboardVisible = false;
                    }
                };

                keyboard.PropertyChanged += (s, e) =>
                {
                    if (e.Property == Visual.IsVisibleProperty && !keyboard.IsVisible)
                    {
                        Dispatcher.UIThread.Post(() => 
                        {
                            if (navigatingToResults)
                            {
                                if (list != null && list.ItemCount > 0)
                                {
                                    list.SelectedIndex = 0;
                                    var container = list.ContainerFromIndex(0);
                                    if (container is ListBoxItem item)
                                    {
                                        item.Focus();
                                    }
                                    else
                                    {
                                        list.Focus();
                                    }
                                }
                                navigatingToResults = false;
                            }
                            else
                            {
                                box.Focus();
                            }
                        }, DispatcherPriority.Input);
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
