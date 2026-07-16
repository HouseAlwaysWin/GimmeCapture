using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace GimmeCapture.Converters;

/// <summary>
/// Sanitizes text shown in a <c>SelectableTextBlock</c> so it can never trigger Avalonia's selection
/// render crash. Avalonia 12's <c>SelectableTextBlock.RenderTextLayout</c> hit-tests the selection via
/// <c>TextLayout.HitTestTextRange</c>, which throws <c>"textLength ('0') must be a non-zero value"</c>
/// from <c>GetTextBounds</c> when the selection spans a ZERO-LENGTH text line — i.e. a blank line or a
/// trailing newline. LLM/OCR translation output routinely contains those, so selecting/copying a pinned
/// translation crashed the whole app.
/// <para>
/// The fix normalizes line endings, drops any trailing whitespace/newlines, and replaces internal
/// zero-length lines with a single space — so no line is ever empty (paragraph spacing is preserved as a
/// space-height line) while the displayed/selectable text stays visually identical.
/// </para>
/// </summary>
public sealed class SelectableTextSanitizeConverter : IValueConverter
{
    public static readonly SelectableTextSanitizeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Sanitize(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>Returns text with no zero-length lines (see the type remarks). Null/empty passes through.</summary>
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        string[] lines = normalized.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0)
            {
                lines[i] = " "; // keep paragraph spacing without a crash-triggering empty line
            }
        }

        return string.Join("\n", lines);
    }
}
