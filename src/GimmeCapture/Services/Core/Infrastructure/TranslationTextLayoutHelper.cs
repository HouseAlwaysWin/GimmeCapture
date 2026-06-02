using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace GimmeCapture.Services.Core.Infrastructure;

internal readonly record struct TranslationTextLayoutMetrics(
    Rect Bounds,
    double ContentHeight,
    double FontSize,
    bool IsOverflowing);

internal static class TranslationTextLayoutHelper
{
    private const double MinFontSize = 6.0;
    private const double MaxFontSize = 72.0;
    private const double HorizontalPadding = 12.0;
    private const double VerticalPadding = 12.0;
    private const double OverflowTolerance = 2.0;

    public static TranslationTextLayoutMetrics FitBounds(
        string text,
        Rect currentBounds,
        double preferredFontSize,
        Size viewportSize)
    {
        Rect bounded = ClampToViewport(currentBounds, viewportSize);
        double fontSize = Math.Clamp(preferredFontSize, MinFontSize, MaxFontSize);
        double usableWidth = Math.Max(40, bounded.Width - HorizontalPadding);
        double usableHeight = Math.Max(20, bounded.Height - VerticalPadding);

        var desired = MeasureText(text, fontSize, usableWidth);
        while ((desired.Height > usableHeight + OverflowTolerance || desired.Width > usableWidth + OverflowTolerance) && fontSize > MinFontSize)
        {
            fontSize = Math.Max(MinFontSize, fontSize - 0.5);
            desired = MeasureText(text, fontSize, usableWidth);
        }

        bool overflowing =
            desired.Height > usableHeight + OverflowTolerance
            || desired.Width > usableWidth + OverflowTolerance
            || fontSize < (preferredFontSize - 0.1);
        return new TranslationTextLayoutMetrics(bounded, desired.Height, fontSize, overflowing);
    }

    private static Size MeasureText(string text, double fontSize, double usableWidth)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            FontFamily = LocalizationService.Instance.CurrentFontFamily,
            FontSize = fontSize,
            TextWrapping = TextWrapping.Wrap
        };

        textBlock.Measure(new Size(usableWidth, double.PositiveInfinity));
        return textBlock.DesiredSize;
    }

    private static Rect ClampToViewport(Rect bounds, Size viewportSize)
    {
        double width = Math.Max(40, Math.Min(bounds.Width, Math.Max(40, viewportSize.Width)));
        double height = Math.Max(24, Math.Min(bounds.Height, Math.Max(24, viewportSize.Height)));
        double x = bounds.X;
        double y = bounds.Y;

        if (x + width > viewportSize.Width)
        {
            x = Math.Max(0, viewportSize.Width - width);
        }

        if (y + height > viewportSize.Height)
        {
            y = Math.Max(0, viewportSize.Height - height);
        }

        return new Rect(x, y, width, height);
    }
}
