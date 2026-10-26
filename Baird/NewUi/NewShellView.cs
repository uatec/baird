using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.Configuration;

namespace Baird.NewUi
{
    /// <summary>The new UI's root. A placeholder until the shell lands (phase 1, task 5).</summary>
    public class NewShellView : UserControl
    {
        public NewShellView(IConfiguration config)
        {
            Background = Brushes.Transparent;
            Content = new TextBlock { Text = "New UI", Foreground = Brushes.White, FontSize = 48, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
    }
}
