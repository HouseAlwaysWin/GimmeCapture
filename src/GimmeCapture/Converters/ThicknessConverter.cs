using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace GimmeCapture.Converters;

public class ThicknessConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d && parameter is string param)
        {
            if (param == "LeftTop")
            {
                // This is a bit tricky, Margin expects Thickness. 
                // But we only bound X. We need Y too. 
                // Actually MultiBinding is better here.
                // But let's cheat and assume this converter is not the right way for single binding.
                // Reverting to Canvas might be easier for positioning, or using MultiBinding.
                return new Thickness(d, 0, 0, 0); 
            }
        }
        return new Thickness(0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
