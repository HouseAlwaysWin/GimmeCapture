using System;

namespace GimmeCapture.Services.Translation;

internal static class OcrTextQualityPolicy
{
    public static bool IsUseful(string text, float confidence)
    {
        if (string.IsNullOrWhiteSpace(text) || confidence < 0.10f)
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
        if (trimmed.IsEmpty)
        {
            return false;
        }

        bool sawUseful = false;
        bool allPlaceholder = true;
        int usefulCount = 0;
        int suspiciousCount = 0;
        foreach (char ch in trimmed)
        {
            if (IsInvalidCharacter(ch))
            {
                return false;
            }

            bool isPlaceholder = ch is '?' or '.' or '-' or '_' or '*';
            allPlaceholder &= isPlaceholder;

            if (char.IsLetterOrDigit(ch)
                || (ch >= 0x4E00 && ch <= 0x9FFF)
                || (ch >= 0x3040 && ch <= 0x309F)
                || (ch >= 0x30A0 && ch <= 0x30FF)
                || (ch >= 0xAC00 && ch <= 0xD7AF))
            {
                sawUseful = true;
                usefulCount++;
            }
            else if (!char.IsWhiteSpace(ch) && !char.IsPunctuation(ch))
            {
                suspiciousCount++;
            }
        }

        return sawUseful
            && !allPlaceholder
            && suspiciousCount <= usefulCount
            && (usefulCount > 2 || confidence >= 0.30f);
    }

    private static bool IsInvalidCharacter(char ch)
    {
        return ch == '\uFFFD'
            || char.IsControl(ch)
            || ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF'
            || (ch >= '\uD800' && ch <= '\uDFFF')
            || (ch >= '\uE000' && ch <= '\uF8FF');
    }
}
