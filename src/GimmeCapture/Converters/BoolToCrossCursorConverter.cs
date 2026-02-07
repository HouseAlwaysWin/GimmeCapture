using Avalonia.Data.Converters;
using Avalonia.Input;
using System;
using System.Globalization;

namespace GimmeCapture.Converters
{
    public class BoolToCrossCursorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b && b)
            {
                return new Cursor(StandardCursorType.Cross);
            }
            return Cursor.Default;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
