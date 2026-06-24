using System;

namespace GimmeCapture.Views.Main;

/// <summary>
/// Pure fit-to-screen math for pinned floating images (notably tall/wide
/// scrolling-capture stitches). Extracted from the inline clamp in
/// <c>SnipWindow.ViewModelWiring.cs</c> so it can be unit tested without a UI
/// thread. Behavior-preserving.
/// </summary>
/// <remarks>
/// The fit only ever scales <em>down</em> the displayed window so every resize
/// handle stays on-screen; it never enlarges content. The full-resolution
/// bitmap is untouched, so save/copy/export resolution is unaffected.
/// </remarks>
internal static class PinFitMath
{
    /// <summary>Horizontal slack left around a clamped pin (95% of work-area width).</summary>
    public const double DefaultWidthMargin = 0.95;

    /// <summary>Vertical slack left around a clamped pin (90% of work-area height).</summary>
    public const double DefaultHeightMargin = 0.90;

    /// <summary>
    /// Returns the uniform scale factor to apply to a pin's content so it fits the
    /// available work area, preserving aspect ratio. Returns <c>1.0</c> when the
    /// content already fits (or when inputs are non-positive / non-finite), so the
    /// caller can treat anything &lt; 1.0 as "clamped".
    /// </summary>
    /// <param name="contentWidth">Logical window content width (image + decoration padding).</param>
    /// <param name="contentHeight">Logical window content height (image + decoration padding).</param>
    /// <param name="workAreaWidth">Target screen working-area width (logical).</param>
    /// <param name="workAreaHeight">Target screen working-area height (logical).</param>
    /// <param name="widthMargin">Fraction of the work-area width the pin may occupy.</param>
    /// <param name="heightMargin">Fraction of the work-area height the pin may occupy.</param>
    public static double ComputeFitScale(
        double contentWidth,
        double contentHeight,
        double workAreaWidth,
        double workAreaHeight,
        double widthMargin = DefaultWidthMargin,
        double heightMargin = DefaultHeightMargin)
    {
        if (contentWidth <= 0 || contentHeight <= 0 || workAreaWidth <= 0 || workAreaHeight <= 0)
        {
            return 1.0;
        }

        double fit = Math.Min(
            (workAreaWidth * widthMargin) / contentWidth,
            (workAreaHeight * heightMargin) / contentHeight);

        if (!double.IsFinite(fit) || fit <= 0 || fit >= 1.0)
        {
            // Already fits (or degenerate input) — never enlarge.
            return 1.0;
        }

        return fit;
    }
}
