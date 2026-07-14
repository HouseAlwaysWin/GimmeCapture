using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Input;
using GimmeCapture.Models;
using GimmeCapture.Views.Shared;

namespace GimmeCapture.Converters;

/// <summary>
/// Maps the active annotation tool (+ the point-removal flag) to the content cursor, bound directly on
/// the pinned image so the tool's cursor shows on hover and updates the instant the tool changes — not
/// only while the pointer is captured (which is what a window-level cursor would give).
/// Bind order: [CurrentAnnotationTool (AnnotationType), IsPointRemovalMode (bool)].
/// </summary>
public sealed class AnnotationToolToCursorConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        // "Click to remove background" (point-removal) takes precedence and shows the crosshair.
        if (values.Count > 1 && values[1] is bool pointRemoval && pointRemoval)
        {
            return new Cursor(StandardCursorType.Cross);
        }

        var tool = values.Count > 0 && values[0] is AnnotationType t ? t : AnnotationType.None;
        return DrawingToolCursors.ForTool(tool);
    }
}
