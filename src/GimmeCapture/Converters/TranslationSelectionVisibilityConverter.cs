using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace GimmeCapture.Converters;

public sealed class TranslationSelectionVisibilityConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        string originalText = values.Count > 0 ? values[0] as string ?? string.Empty : string.Empty;
        string translatedText = values.Count > 1 ? values[1] as string ?? string.Empty : string.Empty;
        bool showOcrResult = values.Count > 2 && values[2] is bool show && show;
        bool isCapturing = values.Count > 3 && values[3] is bool capturing && capturing;
        bool showTranslationResults = values.Count > 4 && values[4] is bool showResults && showResults;

        if (isCapturing || !showTranslationResults)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(translatedText))
        {
            return true;
        }

        return showOcrResult && !string.IsNullOrWhiteSpace(originalText);
    }
}
