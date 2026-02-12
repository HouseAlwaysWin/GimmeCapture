using System;
using System.Globalization;
using Avalonia.Data.Converters;
using GimmeCapture.Services; // Assuming this is correct, but find_by_name will confirm
using GimmeCapture.Services.Core; // Just in case

namespace GimmeCapture.Converters;

public class EnumToLocalizedConverter : IValueConverter
{
    public static readonly EnumToLocalizedConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Enum enumValue)
        {
            string prefix = parameter as string ?? string.Empty;
            string key = $"{prefix}{enumValue}";
            
            // Try to find localized string
            string localized = LocalizationService.Instance[key];
            
            // Fallback to enum name if localization missing (or if it returns key itself)
            if (string.IsNullOrEmpty(localized) || localized == key)
            {
                return enumValue.ToString();
            }
            
            return localized;
        }

        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
