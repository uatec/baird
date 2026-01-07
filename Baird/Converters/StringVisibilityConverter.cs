using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Baird.Converters
{
    public class StringVisibilityConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string s)
            {
                return !string.IsNullOrEmpty(s);
            }
            return false;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
