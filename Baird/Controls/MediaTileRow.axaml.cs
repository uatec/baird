using Avalonia;
using Avalonia.Controls;
using System.Windows.Input;

namespace Baird.Controls
{
    public partial class MediaTileRow : UserControl
    {
        public static readonly StyledProperty<ICommand> CommandProperty =
            AvaloniaProperty.Register<MediaTileRow, ICommand>(nameof(Command));

        public ICommand Command
        {
            get => GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public static readonly StyledProperty<ICommand> LongPressCommandProperty =
            AvaloniaProperty.Register<MediaTileRow, ICommand>(nameof(LongPressCommand));

        public ICommand LongPressCommand
        {
            get => GetValue(LongPressCommandProperty);
            set => SetValue(LongPressCommandProperty, value);
        }

        public MediaTileRow()
        {
            InitializeComponent();
        }
    }
}
