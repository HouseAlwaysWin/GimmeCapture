using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace GimmeCapture.Converters;

public class BoolToThicknessConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool boolVal = value is bool b && b;
        double thickness = 1.0;

        if (parameter != null)
        {
            if (parameter is double d) thickness = d;
            else if (parameter is int i) thickness = i;
            else if (parameter is string s && double.TryParse(s, out var ps)) thickness = ps;
        }

        return boolVal ? new Thickness(thickness) : new Thickness(0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
