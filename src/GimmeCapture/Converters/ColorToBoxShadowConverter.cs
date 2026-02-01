using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace GimmeCapture.Converters;

public class ColorToBoxShadowConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color color)
        {
            // Create a semi-transparent version for the shadow (0.5 opacity)
            var shadowColor = Color.FromArgb(0x80, color.R, color.G, color.B);
            return new BoxShadows(new BoxShadow
            {
                Blur = 10,
                Spread = 1,
                Color = shadowColor
            });
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
