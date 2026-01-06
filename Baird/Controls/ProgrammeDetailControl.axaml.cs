using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Baird.Services;
using System;

namespace Baird.Controls
{
    public partial class ProgrammeDetailControl : UserControl
    {
        public event EventHandler<MediaItem>? EpisodeChosen;
        public event EventHandler? BackRequested;

        public ProgrammeDetailControl()
        {
            InitializeComponent();
            
            var backBtn = this.FindControl<Button>("BackButton");
            if (backBtn != null)
            {
                backBtn.Click += (s, e) => BackRequested?.Invoke(this, EventArgs.Empty);
                // Also ensure key down is hooked up
                backBtn.KeyDown += BackButton_KeyDown; 
            }

            var list = this.FindControl<ListBox>("EpisodeList");
            if (list != null)
            {
                // Use Tunneling to capture keys before ListBox handles them
                list.AddHandler(KeyDownEvent, OnEpisodeListKeyDown, RoutingStrategies.Tunnel); 
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnEpisodeListKeyDown(object? sender, KeyEventArgs e)
        {
             Console.WriteLine($"[ProgrammeDetailControl] PreviewKeyDown: {e.Key} Source: {sender?.GetType().Name}");
             
             if (sender is ListBox list)
             {
                 if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down)
                 {
                      var count = list.ItemCount;
                      var index = list.SelectedIndex;
                      if (index < 0) index = 0;

                      int cols = 1;
                      if (list.ItemsPanelRoot is WrapPanel panel)
                      {
                          var panelWidth = panel.Bounds.Width;
                          var itemWidth = panel.ItemWidth; 
                          if (itemWidth <= 0) itemWidth = 300; // fallback
                          cols = (int)Math.Floor(panelWidth / itemWidth);
                      }
                      
                      // Fallback if cols calculation fails or logic is weird
                      if (cols < 1) cols = 1;
                      if (cols > 6) cols = 6; // Safety cap?

                      Console.WriteLine($"[ProgrammeDetailControl] Nav calc: Index={index} Count={count} Cols={cols}");

                      int newIndex = index;
                      bool handled = false;

                      switch (e.Key)
                      {
                          case Key.Right:
                              newIndex = index + 1;
                              if (newIndex < count) handled = true;
                              break;
                          case Key.Left:
                              newIndex = index - 1;
                              if (newIndex >= 0) handled = true;
                              break;
                          case Key.Down:
                              newIndex = index + cols;
                              if (newIndex >= count) newIndex = count - 1; // Clamp to last item
                              if (newIndex != index) handled = true;
                              break;
                          case Key.Up:
                              newIndex = index - cols;
                              if (newIndex < 0)
                              {
                                   // Escape to Back Button
                                   Console.WriteLine("[ProgrammeDetailControl] Escaping Up to BackButton");
                                   var backBtn = this.FindControl<Button>("BackButton");
                                   backBtn?.Focus();
                                   e.Handled = true;
                                   return; 
                              }
                              handled = true;
                              break;
                      }

                      if (handled && newIndex >= 0 && newIndex < count)
                      {
                          Console.WriteLine($"[ProgrammeDetailControl] Moving to Index {newIndex}");
                          list.SelectedIndex = newIndex;
                          e.Handled = true;
                      }
                 }
                 else if (e.Key == Key.Enter || e.Key == Key.Return)
                 {
                     if (list.SelectedItem is MediaItem item)
                     {
                         EpisodeChosen?.Invoke(this, item);
                         e.Handled = true;
                     }
                 }
             }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == Visual.IsVisibleProperty && change.NewValue is bool b && b)
            {
                FocusFirstItem();
            }
        }

        private async void FocusFirstItem()
        {
             var list = this.FindControl<ListBox>("EpisodeList");
             if (list == null) return;
             
             // Retry waiting for data
             for(int i=0; i<20; i++)
             {
                 if (list.ItemCount > 0)
                 {
                      // Ensure layout is up to date to get container
                      var container = list.ContainerFromIndex(0);
                      if (container == null)
                      {
                          list.UpdateLayout();
                          container = list.ContainerFromIndex(0);
                      }
                      
                      if (container is Control c && c.Focusable)
                      {
                          Console.WriteLine($"[ProgrammeDetailControl] Focusing Container: {c.GetType().Name}");
                          c.Focus();
                          list.SelectedIndex = 0;
                          return;
                      }
                 }
                 await System.Threading.Tasks.Task.Delay(100);
             }
        }

        private void BackButton_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                var list = this.FindControl<ListBox>("EpisodeList");
                if (list != null && list.ItemCount > 0)
                {
                    Console.WriteLine("[ProgrammeDetailControl] BackButton Down -> Focusing List");
                    list.Focus();
                    list.SelectedIndex = 0;
                    e.Handled = true;
                }
            }
        }
    }
}
