using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace GimmeCapture.Converters
{
    public class ThicknessToLeftTopMarginConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Thickness t)
            {
                // We keep the Left and Top, set Right to 0, and maybe a small Bottom if needed.
                // But specifically for this task, the user wants it above the image.
                // The image starts at WindowPadding.Top.
                // The diagnostic text is in Row 0.
                // So if we give it Margin Left=t.Left, it will align horizontally.
                return new Thickness(t.Left, 4, 0, 0);
            }
            return new Thickness(10, 4, 0, 0);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
