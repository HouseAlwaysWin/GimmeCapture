using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace GimmeCapture.Converters;

public class DynamicThicknessConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double baseVal = 0;
        if (value is double d) baseVal = d;
        else if (value is int i) baseVal = i;
        else if (value is float f) baseVal = f;

        double offset = 0;
        if (parameter != null)
        {
            if (parameter is double pd) offset = pd;
            else if (parameter is int pi) offset = pi;
            else if (parameter is string s && double.TryParse(s, out var ps)) offset = ps;
        }

        return new Thickness(baseVal + offset);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
