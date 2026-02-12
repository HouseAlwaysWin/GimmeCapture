using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace GimmeCapture.Converters
{
    /// <summary>
    /// Returns 0.0 or 0.01 opacity when true, and 1.0 when false.
    /// In Avalonia, Opacity="0" elements are still hit-testable if IsHitTestVisible is true and Background is not null.
    /// We use 0.01 for safety to ensure it remains in the hit-test tree on all platforms.
    /// </summary>
    public class BoolToHiddenOpacityConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool b)
                return b ? 0.01 : 1.0;
            return 1.0;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
