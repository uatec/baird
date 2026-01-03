using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Baird.Converters
{
    public class BoolToPlayPauseConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isPaused && isPaused)
            {
                return "Resume";
            }
            return "Pause";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
