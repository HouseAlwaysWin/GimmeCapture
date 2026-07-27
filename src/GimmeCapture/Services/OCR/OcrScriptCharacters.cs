using System;

namespace GimmeCapture.Services.OCR;

/// <summary>
/// Character-class counters used to judge which script a recogniser actually produced. Kept separate from any ONNX
/// session so the language-probe decision stays a pure function.
///
/// Ranges are written as escapes rather than literal glyphs: a boundary like '\u9FFF' is checkable against the
/// Unicode charts, whereas the glyph it stands for is not something a reviewer can identify by eye.
/// </summary>
public static class OcrScriptCharacters
{
    /// <summary>
    /// Hiragana + katakana, excluding the combining voiced marks (U+3099-U+309C) and the katakana middle dot
    /// (U+30FB), which appear as ordinary punctuation in Chinese text too and would be false evidence. The
    /// prolonged sound mark U+30FC is deliberately kept — it appears in Japanese and essentially nowhere else.
    /// </summary>
    public static int CountKana(string? text) => Count(text, IsKana);

    /// <summary>Precomposed hangul syllables.</summary>
    public static int CountHangul(string? text) => Count(text, IsHangul);

    /// <summary>CJK unified ideographs — shared by Chinese and Japanese, so this alone never identifies a language.</summary>
    public static int CountCjk(string? text) => Count(text, IsCjk);

    public static int CountLatinLetters(string? text) => Count(text, IsLatinLetter);

    /// <summary>Characters that belong to some real script: letters, digits, or CJK/kana/hangul.</summary>
    public static int CountUseful(string? text) => Count(text, IsUseful);

    /// <summary>
    /// Characters that belong to no script and are not whitespace or punctuation — box-drawing glyphs, dingbats and
    /// the like. A recogniser reading a script it was not trained for emits these in bulk, which is the signal.
    /// </summary>
    public static int CountSuspicious(string? text) =>
        Count(text, static ch => !IsUseful(ch) && !char.IsWhiteSpace(ch) && !char.IsPunctuation(ch));

    private static int Count(string? text, Func<char, bool> predicate)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        int count = 0;
        foreach (char ch in text)
        {
            if (predicate(ch))
            {
                count++;
            }
        }

        return count;
    }

    private static bool IsKana(char ch) =>
        (ch >= '\u3041' && ch <= '\u3096')      // hiragana
        || (ch >= '\u309D' && ch <= '\u309F')   // hiragana iteration marks
        || (ch >= '\u30A1' && ch <= '\u30FA')   // katakana
        || (ch >= '\u30FC' && ch <= '\u30FF');  // prolonged sound mark + katakana iteration marks

    private static bool IsHangul(char ch) => ch >= '\uAC00' && ch <= '\uD7AF';

    private static bool IsCjk(char ch) => ch >= '\u4E00' && ch <= '\u9FFF';

    private static bool IsLatinLetter(char ch) => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    // Deliberately the WHOLE kana block, unlike IsKana: strict for evidence (a character that proves the script),
    // lenient for usefulness (a character that is not garbage). Combining voiced marks are legitimate output even
    // though they prove nothing on their own, and penalising them as garbage would skew the score.
    private static bool IsUseful(char ch) =>
        char.IsLetterOrDigit(ch)
        || IsCjk(ch)
        || (ch >= '぀' && ch <= 'ヿ')
        || IsHangul(ch);
}
