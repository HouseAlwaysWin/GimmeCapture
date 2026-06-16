using System;
using System.Collections.Generic;
using System.Text;

namespace GimmeCapture.Services.Translation;

internal static class OcrTextSanitizer
{
    public static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string[] rawLines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>(rawLines.Length);

        foreach (string rawLine in rawLines)
        {
            var builder = new StringBuilder(rawLine.Length);
            bool lastWasSpace = false;

            foreach (char ch in rawLine)
            {
                if (IsInvalidCharacter(ch))
                {
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                {
                    if (!lastWasSpace)
                    {
                        builder.Append(' ');
                        lastWasSpace = true;
                    }

                    continue;
                }

                lastWasSpace = false;
                builder.Append(ch);
            }

            string cleaned = NormalizeTerminalPunctuation(builder.ToString().Trim());
            if (string.IsNullOrWhiteSpace(cleaned) || IsLowSignalLine(cleaned))
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

    private static string NormalizeTerminalPunctuation(string text)
    {
        if (text.Length < 2)
        {
            return text;
        }

        char last = text[^1];
        char previous = text[^2];
        bool followsCjkText = (previous >= '\u4E00' && previous <= '\u9FFF')
            || (previous >= '\u3040' && previous <= '\u30FF');

        return followsCjkText && last is '\u00B7' or '\u2022' or '\u2027'
            ? text[..^1] + '\u3002'
            : text;
    }

    private static bool IsLowSignalLine(string line)
    {
        int usefulCount = 0;
        int suspiciousCount = 0;
        int punctuationCount = 0;

        foreach (char ch in line)
        {
            if (char.IsLetterOrDigit(ch)
                || (ch >= 0x4E00 && ch <= 0x9FFF)
                || (ch >= 0x3040 && ch <= 0x30FF)
                || (ch >= 0xAC00 && ch <= 0xD7AF))
            {
                usefulCount++;
                continue;
            }

            if (char.IsPunctuation(ch))
            {
                punctuationCount++;
                continue;
            }

            if (!char.IsWhiteSpace(ch))
            {
                suspiciousCount++;
            }
        }

        return usefulCount == 0
            || suspiciousCount > usefulCount
            || (usefulCount <= 1 && punctuationCount + suspiciousCount >= 2);
    }

    private static bool IsInvalidCharacter(char ch)
    {
        return ch == '\uFFFD'
            || char.IsControl(ch)
            || ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF'
            || (ch >= '\uD800' && ch <= '\uDFFF')
            || (ch >= '\uE000' && ch <= '\uF8FF');
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
