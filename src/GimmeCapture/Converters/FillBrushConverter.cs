using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.Converters;

/// <summary>True when the given <see cref="AnnotationType"/> supports a fill toggle (Rectangle/Ellipse).</summary>
public class FillSupportedConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AnnotationType type && AnnotationEditorState.SupportsFill(type);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Maps [Color, bool IsFilled] to a fill brush: the color when filled, otherwise transparent.
/// Used so outlined shapes (Rectangle/Ellipse) can optionally show a solid fill.
/// </summary>
public class FillBrushConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count >= 2 && values[0] is Color color && values[1] is bool isFilled && isFilled)
        {
            return new SolidColorBrush(color);
        }

        return Brushes.Transparent;
    }
}
