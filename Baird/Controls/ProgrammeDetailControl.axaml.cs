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
        public ProgrammeDetailControl()
        {
            InitializeComponent();
            
            // Re-focus when DataContext changes (happens when ContentControl reuses this view for a new ViewModel)
            this.GetObservable(DataContextProperty).Subscribe(dc =>
            {
                if (dc is ViewModels.ProgrammeDetailViewModel)
                {
                    Console.WriteLine("[ProgrammeDetailControl] DataContext changed, re-running focus logic");
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        this.Focusable = true;
                        this.Focus();
                        FocusFirstItem();
                    }, Avalonia.Threading.DispatcherPriority.Input);
                }
            });
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }


        
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            this.Focusable = true;
            this.Focus();
            FocusFirstItem();
        }

        private async void FocusFirstItem()
        {
             var list = this.FindControl<ItemsControl>("EpisodeList");
             if (list == null) return;
             
             // Retry waiting for data
             for(int i=0; i<20; i++)
             {
                 // Bail out if we've been detached from the tree
                 if (this.GetVisualRoot() == null) return;

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
