using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace GimmeCapture.Converters;

public class ColorToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color color) return new SolidColorBrush(color);
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
