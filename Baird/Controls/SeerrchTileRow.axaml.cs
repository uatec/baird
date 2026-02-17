using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace Baird.Controls
{
    public partial class SeerrchTileRow : UserControl
    {
        public static readonly StyledProperty<ICommand> CommandProperty =
            AvaloniaProperty.Register<SeerrchTileRow, ICommand>(nameof(Command));

        public ICommand Command
        {
            get => GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public SeerrchTileRow()
        {
            InitializeComponent();
        }
    }
}
