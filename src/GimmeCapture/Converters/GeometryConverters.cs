using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace GimmeCapture.Converters;

public class RectXConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Point p1 && parameter is Point p2) return Math.Min(p1.X, p2.X);
        return 0;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class RectYConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Point p1 && parameter is Point p2) return Math.Min(p1.Y, p2.Y);
        return 0;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class RectWidthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Point p1 && parameter is Point p2) return Math.Abs(p1.X - p2.X);
        return 0;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class RectHeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Point p1 && parameter is Point p2) return Math.Abs(p1.Y - p2.Y);
        return 0;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
