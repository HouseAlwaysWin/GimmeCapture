using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GimmeCapture.Converters
{
    public class ModifiedColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isModified && isModified)
            {
                return Brush.Parse("#E60012"); // Red accent
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
