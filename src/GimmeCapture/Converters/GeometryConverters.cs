using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using GimmeCapture.Models;

namespace GimmeCapture.Converters;

public class RectXConverter : IValueConverter, IMultiValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Point p1 && parameter is Point p2) return Math.Min(p1.X, p2.X);
        return 0;
    }
    
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && values[0] is Point p1 && values[1] is Point p2) return Math.Min(p1.X, p2.X);
        return 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class RectYConverter : IValueConverter, IMultiValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Point p1 && parameter is Point p2) return Math.Min(p1.Y, p2.Y);
        return 0;
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && values[0] is Point p1 && values[1] is Point p2) return Math.Min(p1.Y, p2.Y);
        return 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class RectWidthConverter : IValueConverter, IMultiValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Point p1 && parameter is Point p2) return Math.Abs(p1.X - p2.X);
        return 0;
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && values[0] is Point p1 && values[1] is Point p2) return Math.Abs(p1.X - p2.X);
        return 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class RectHeightConverter : IValueConverter, IMultiValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Point p1 && parameter is Point p2) return Math.Abs(p1.Y - p2.Y);
        return 0;
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && values[0] is Point p1 && values[1] is Point p2) return Math.Abs(p1.Y - p2.Y);
        return 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class AnnotationToLeftConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 3 && values[0] is AnnotationType type && values[1] is Point p1 && values[2] is Point p2)
        {
            if (type == AnnotationType.Rectangle || type == AnnotationType.Ellipse)
                return Math.Min(p1.X, p2.X);
            if (type == AnnotationType.Text)
                return p1.X;
            return 0.0; // Arrow, Line, Pen use relative coordinates or internal geometry offsets
        }
        return 0.0;
    }
    public object?[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class AnnotationToTopConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 3 && values[0] is AnnotationType type && values[1] is Point p1 && values[2] is Point p2)
        {
            if (type == AnnotationType.Rectangle || type == AnnotationType.Ellipse)
                return Math.Min(p1.Y, p2.Y);
            if (type == AnnotationType.Text)
                return p1.Y;
            return 0.0; // Arrow, Line, Pen
        }
        return 0.0;
    }
    public object?[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class PenGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is IEnumerable<Point> pointsEnumerable)
        {
            var points = pointsEnumerable.ToList();
            if (points.Count > 0)
            {
                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    var first = points[0];
                    context.BeginFigure(first, false);
                    foreach (var point in points.Skip(1))
                    {
                        context.LineTo(point);
                    }
                    context.EndFigure(false);
                }
                return geometry;
            }
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class PointsToRectConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && values[0] is Point p1 && values[1] is Point p2)
        {
            return new Rect(
                Math.Min(p1.X, p2.X),
                Math.Min(p1.Y, p2.Y),
                Math.Abs(p1.X - p2.X),
                Math.Abs(p1.Y - p2.Y)
            );
        }
        return new Rect(0, 0, 0, 0);
    }

    public object?[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
public class PointsToInverseTranslateConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && values[0] is Point p1 && values[1] is Point p2)
        {
            return new TranslateTransform(-Math.Min(p1.X, p2.X), -Math.Min(p1.Y, p2.Y));
        }
        return new TranslateTransform(0, 0);
    }

    public object?[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
