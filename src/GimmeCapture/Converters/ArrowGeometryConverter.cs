using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GimmeCapture.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace GimmeCapture.Converters;

public class ArrowGeometryConverter : IValueConverter, IMultiValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Annotation a && a.Type == AnnotationType.Arrow)
        {
            return CreateArrow(a.StartPoint, a.EndPoint, a.Thickness);
        }
        return null;
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 3 && values[0] is Point p1 && values[1] is Point p2 && values[2] is double thickness)
        {
            return CreateArrow(p1, p2, thickness);
        }
        return null;
    }

    private object CreateArrow(Point p1, Point p2, double thickness)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(p1, false);
            context.LineTo(p2);
            
            // Snipaste-like arrow head with an inner slanted notch.
            var dx = p2.X - p1.X;
            var dy = p2.Y - p1.Y;
            var len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 0.001)
            {
                var ux = dx / len;
                var uy = dy / len;
                var px = -uy;
                var py = ux;

                var headLength = Math.Clamp(8.0 + (thickness * 1.4), 8.0, 18.0);
                // Softer inner notch angle (less steep).
                var halfWidth = headLength * 0.36;
                var notchDepth = headLength * 0.38;

                var left = new Point(
                    p2.X - (ux * headLength) + (px * halfWidth),
                    p2.Y - (uy * headLength) + (py * halfWidth));
                var right = new Point(
                    p2.X - (ux * headLength) - (px * halfWidth),
                    p2.Y - (uy * headLength) - (py * halfWidth));
                var notch = new Point(
                    p2.X - (ux * notchDepth),
                    p2.Y - (uy * notchDepth));

                context.BeginFigure(p2, true);
                context.LineTo(left);
                context.LineTo(notch);
                context.LineTo(right);
                context.EndFigure(true);
            }
        }
        return geometry;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
