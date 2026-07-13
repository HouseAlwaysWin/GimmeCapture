using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Input;

namespace GimmeCapture.Converters;

public class StringToKeyGestureConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hotkeyStr && !string.IsNullOrEmpty(hotkeyStr))
        {
            try
            {
                return KeyGesture.Parse(hotkeyStr);
            }
            catch (Exception ex)
            {
                GimmeCapture.Services.Core.Infrastructure.AppLog.Warning("StringToKeyGesture.Convert", ex);
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString();
    }
}
