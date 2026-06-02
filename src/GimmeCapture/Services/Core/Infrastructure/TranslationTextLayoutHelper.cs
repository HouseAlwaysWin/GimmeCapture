using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GimmeCapture.Services.Core.Infrastructure;

internal readonly record struct TranslationTextLayoutMetrics(
    Rect Bounds,
    double ContentHeight,
    double FontSize);

internal static class TranslationTextLayoutHelper
{
    private const double MinFontSize = 8.0;
    private const double MaxFontSize = 72.0;
    private const double MinWidth = 80.0;
    private const double MinHeight = 50.0;
    private const double HorizontalPadding = 12.0;
    private const double VerticalPadding = 12.0;

    public static TranslationTextLayoutMetrics FitBounds(
        string text,
        Rect currentBounds,
        double preferredFontSize,
        Size viewportSize)
    {
        double fontSize = Math.Clamp(preferredFontSize, MinFontSize, MaxFontSize);
        double width = Math.Max(currentBounds.Width, MinWidth);
        double height = Math.Max(currentBounds.Height, MinHeight);
        double maxWidth = Math.Max(width, Math.Min(Math.Max(width, viewportSize.Width * 0.65), viewportSize.Width - 16));
        double maxHeight = Math.Max(height, Math.Min(Math.Max(height, viewportSize.Height * 0.7), viewportSize.Height - 16));

        var desired = MeasureText(text, fontSize, width);

        while (desired.Height + VerticalPadding > height && width < maxWidth)
        {
            width = Math.Min(maxWidth, width + Math.Max(24.0, fontSize * 2.0));
            desired = MeasureText(text, fontSize, width);
        }

        height = Math.Max(height, desired.Height + VerticalPadding);

        while (height > maxHeight && fontSize > MinFontSize)
        {
            fontSize = Math.Max(MinFontSize, fontSize - 0.5);
            desired = MeasureText(text, fontSize, width);
            height = Math.Max(currentBounds.Height, desired.Height + VerticalPadding);

            if (height <= maxHeight)
            {
                break;
            }

            if (width < maxWidth)
            {
                width = Math.Min(maxWidth, width + Math.Max(16.0, fontSize * 1.5));
                desired = MeasureText(text, fontSize, width);
                height = Math.Max(currentBounds.Height, desired.Height + VerticalPadding);
            }
        }

        width = Math.Max(width, Math.Min(maxWidth, desired.Width + HorizontalPadding));
        height = Math.Min(maxHeight, Math.Max(height, desired.Height + VerticalPadding));

        var fittedBounds = ClampToViewport(
            new Rect(currentBounds.X, currentBounds.Y, width, height),
            viewportSize);

        return new TranslationTextLayoutMetrics(fittedBounds, desired.Height, fontSize);
    }

    private static Size MeasureText(string text, double fontSize, double boundsWidth)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = LocalizationService.Instance.CurrentFontFamily,
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap
        };

        double availableWidth = Math.Max(40, boundsWidth - HorizontalPadding);
        textBlock.Measure(new Size(availableWidth, double.PositiveInfinity));
        return textBlock.DesiredSize;
    }

    private static Rect ClampToViewport(Rect bounds, Size viewportSize)
    {
        double x = bounds.X;
        double y = bounds.Y;

        if (x + bounds.Width > viewportSize.Width)
        {
            x = Math.Max(0, viewportSize.Width - bounds.Width);
        }

        if (y + bounds.Height > viewportSize.Height)
        {
            y = Math.Max(0, viewportSize.Height - bounds.Height);
        }

        return new Rect(x, y, bounds.Width, bounds.Height);
    }
}
