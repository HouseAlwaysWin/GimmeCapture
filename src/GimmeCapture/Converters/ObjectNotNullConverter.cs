using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace GimmeCapture.Converters;

/// <summary>Returns true when the bound value is not null. Used to show controls only when something is selected.</summary>
public class ObjectNotNullConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
