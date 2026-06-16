using GimmeCapture.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace GimmeCapture.Services.Translation;

internal static class TranslationFallbackPolicy
{
    public static bool IsAcceptable(string original, string translated, TranslationLanguage target) =>
        TranslationService.IsTranslationAcceptableCore(original, translated, target);

    public static bool IsJapaneseAcceptable(
        string original,
        string translated,
        string normalizedOriginal,
        string normalizedTranslated) =>
        TranslationService.IsJapaneseTranslationAcceptableCore(
            original,
            translated,
            normalizedOriginal,
            normalizedTranslated);

    public static string PromoteBestEffort(string original, string translated, TranslationLanguage target) =>
        TranslationService.TryPromoteBestEffortTranslationCore(original, translated, target);

    public static string BuildFailureFallbackText(string original, TranslationLanguage target)
    {
        _ = target;
        if (CountMeaningfulChars(original) == 0)
        {
            return string.Empty;
        }

        string[] lines = original.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>(lines.Length);

        foreach (string rawLine in lines)
        {
            string cleaned = OcrTextSanitizer.Sanitize(rawLine);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            string normalized = NormalizeComparisonText(cleaned);
            if (normalized.Length == 0 || !seen.Add(normalized))
            {
                continue;
            }

            kept.Add(cleaned);
        }

        return string.Join(Environment.NewLine, kept).Trim();
    }

    private static int CountMeaningfulChars(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        int count = 0;
        foreach (char ch in text)
        {
            if (char.IsLetterOrDigit(ch)
                || (ch >= '\u4E00' && ch <= '\u9FFF')
                || (ch >= '\u3040' && ch <= '\u30FF')
                || (ch >= '\uAC00' && ch <= '\uD7AF'))
            {
                count++;
            }
        }

        return count;
    }

    private static string NormalizeComparisonText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString().Trim();
    }
}
