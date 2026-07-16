using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace GimmeCapture.Converters;

public sealed class TranslationDisplayTextConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        string originalText = values.Count > 0 ? values[0] as string ?? string.Empty : string.Empty;
        string translatedText = values.Count > 1 ? values[1] as string ?? string.Empty : string.Empty;
        bool showOcrResult = values.Count > 2 && values[2] is bool show && show;

        string result =
            showOcrResult && !string.IsNullOrWhiteSpace(originalText) ? originalText
            : !string.IsNullOrWhiteSpace(translatedText) ? translatedText
            : originalText;

        // Sanitize so the SelectableTextBlocks these feed can't crash Avalonia's selection hit-test on a
        // zero-length (blank / trailing-newline) line — see SelectableTextSanitizeConverter.
        return SelectableTextSanitizeConverter.Sanitize(result);
    }
}
