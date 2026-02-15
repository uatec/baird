using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System.Windows.Input;

namespace Baird.Controls
{
    public partial class MediaTileControl : UserControl
    {
        public static readonly StyledProperty<ICommand> CommandProperty =
            AvaloniaProperty.Register<MediaTileControl, ICommand>(nameof(Command));

        public ICommand Command
        {
            get => GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public static readonly StyledProperty<object> CommandParameterProperty =
            AvaloniaProperty.Register<MediaTileControl, object>(nameof(CommandParameter));

        public object CommandParameter
        {
            get => GetValue(CommandParameterProperty);
            set => SetValue(CommandParameterProperty, value);
        }

        public static readonly StyledProperty<ICommand> LongPressCommandProperty =
            MediaButton.LongPressCommandProperty.AddOwner<MediaTileControl>();

        public ICommand LongPressCommand
        {
            get => GetValue(LongPressCommandProperty);
            set => SetValue(LongPressCommandProperty, value);
        }

        public static readonly StyledProperty<object> LongPressCommandParameterProperty =
            MediaButton.LongPressCommandParameterProperty.AddOwner<MediaTileControl>();

        public object LongPressCommandParameter
        {
            get => GetValue(LongPressCommandParameterProperty);
            set => SetValue(LongPressCommandParameterProperty, value);
        }

        public MediaTileControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }
    }
}
