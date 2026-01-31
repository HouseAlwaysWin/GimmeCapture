using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace GimmeCapture.Converters;

public class BoolToFontStyleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b)
        {
            return FontStyle.Italic;
        }
        return FontStyle.Normal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
