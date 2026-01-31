using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace GimmeCapture.Converters;

public class ColorMatchConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color c1 && parameter is Color c2) return c1 == c2;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
