using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
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

            var list = this.FindControl<ListBox>("EpisodeList");
            if (list != null)
            {
                // Handle Enter Key
                list.KeyDown += (s, e) => 
                {
                    if (e.Key == Avalonia.Input.Key.Enter || e.Key == Avalonia.Input.Key.Return)
                    {
                        if (list.SelectedItem is MediaItem item)
                        {
                            EpisodeChosen?.Invoke(this, item);
                            e.Handled = true;
                        }
                    }
                };

                // Handle Tapped (Mouse/Touch)
                list.Tapped += (s, e) => 
                {
                    // Tapped event bubbles. Source might be visual child.
                    // However, play safe: if we have a selected item (clicked it), invoke it.
                    // Note: Tapped happens after SelectionChanged usually.
                    if (list.SelectedItem is MediaItem item)
                    {
                        EpisodeChosen?.Invoke(this, item);
                        e.Handled = true;
                    }
                };
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
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
                      list.Focus();
                      list.SelectedIndex = 0; 
                      return;
                 }
                 await System.Threading.Tasks.Task.Delay(100);
             }
        }
    }
}
