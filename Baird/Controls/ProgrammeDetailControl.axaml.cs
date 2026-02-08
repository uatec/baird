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
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void OnEpisodeClicked(object? sender, RoutedEventArgs e)
        {
            if (sender is Control control && control.DataContext is MediaItem item)
            {
                EpisodeChosen?.Invoke(this, item);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            
            if (e.Key == Key.Escape || e.Key == Key.Back)
            {
                BackRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
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
             var list = this.FindControl<ItemsControl>("EpisodeList");
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
                      
                      if (container is Visual v)
                      {
                          var button = v.FindDescendantOfType<Button>();
                          if (button != null)
                          {
                              Console.WriteLine($"[ProgrammeDetailControl] Focusing Button: {button.GetType().Name}");
                              button.Focus();
                              return;
                          }
                      }
                 }
                 await System.Threading.Tasks.Task.Delay(100);
             }
        }
    }
}
