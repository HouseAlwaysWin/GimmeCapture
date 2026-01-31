using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GimmeCapture.Models;
using System;
using System.Globalization;

namespace GimmeCapture.Converters;

public class ArrowGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Annotation a && a.Type == AnnotationType.Arrow)
        {
            var p1 = a.StartPoint;
            var p2 = a.EndPoint;
            
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(p1, false);
                context.LineTo(p2);
                
                // Draw Arrow Head
                var angle = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
                var arrowSize = 15.0 + a.Thickness;
                var arrowAngle = Math.PI / 6; // 30 degrees
                
                var ap1 = new Point(
                    p2.X - arrowSize * Math.Cos(angle - arrowAngle),
                    p2.Y - arrowSize * Math.Sin(angle - arrowAngle));
                
                var ap2 = new Point(
                    p2.X - arrowSize * Math.Cos(angle + arrowAngle),
                    p2.Y - arrowSize * Math.Sin(angle + arrowAngle));
                
                context.BeginFigure(p2, true);
                context.LineTo(ap1);
                context.LineTo(ap2);
                context.EndFigure(true);
            }
            return geometry;
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
