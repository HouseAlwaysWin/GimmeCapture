using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace GimmeCapture.Converters;

public class SnapshotOffsetConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && values[0] is Point p1 && values[1] is Point p2)
        {
            var left = Math.Min(p1.X, p2.X);
            var top = Math.Min(p1.Y, p2.Y);
            
            // We want to translate the image by (-left, -top) to align it with the selection start
            return new TranslateTransform(-left, -top);
        }
        return new TranslateTransform(0, 0);
    }
}
