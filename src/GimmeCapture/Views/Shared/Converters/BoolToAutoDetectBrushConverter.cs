using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GimmeCapture.Views.Shared.Converters
{
    public class BoolToAutoDetectBrushConverter : IValueConverter
    {
        public IBrush? ActiveBrush { get; set; } = new SolidColorBrush(Color.Parse("#FF9900")); // Orange for active
        public IBrush? InactiveBrush { get; set; } = new SolidColorBrush(Color.Parse("#44FFFFFF")); // Default semi-transparent white

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isEnabled)
            {
                return isEnabled ? ActiveBrush : InactiveBrush;
            }
            return InactiveBrush;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
