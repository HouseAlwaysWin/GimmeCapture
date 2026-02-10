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
                System.Diagnostics.Debug.WriteLine($"[StringToKeyGestureConverter] Failed to parse hotkey '{hotkeyStr}': {ex.Message}");
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value?.ToString();
    }
}
