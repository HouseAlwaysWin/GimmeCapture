using GimmeCapture.Models;
using GimmeCapture.Services.Translation;

namespace GimmeCapture.Tests;

public sealed class TranslationServiceSanitizationTests
{
    [Theory]
    [InlineData("\u30B7\u30E7\u30FC\u30C8\u30AB\u30C3\u30C8", "\u5FEB\u6377\u9375\uFF08\u30B7\u30E7\u30FC\u30C8\u30AB\u30C3\u30C8\uFF09", "\u5FEB\u6377\u9375")]
    [InlineData("\u30B7\u30E7\u30FC\u30C8\u30AB\u30C3\u30C8", "\u5FEB\u6377\u9375 - \u30B7\u30E7\u30FC\u30C8\u30AB\u30C3\u30C8", "\u5FEB\u6377\u9375")]
    public void SanitizeTranslationCandidate_StripsTrailingJapaneseSourceArtifacts_ForChineseTargets(
        string original,
        string translated,
        string expected)
    {
        string result = TranslationTextSanitizer.Sanitize(original, translated, TranslationLanguage.TraditionalChinese);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CleanupTranslationResult_CollapsesRepeatedTokenCycles()
    {
        string raw = "\u7814\u7A76\u8005\u3001\u7814\u7A76\u54E1\u3001\u7814\u7A76\u8005\u3001\u7814\u7A76\u54E1\u3001\u7814\u7A76\u8005\u3001\u7814\u7A76\u54E1";

        string result = LlamaSharpTranslationEngine.CleanupTranslationResult(raw);

        Assert.Equal("\u7814\u7A76\u8005\u3001\u7814\u7A76\u54E1", result);
    }

    [Fact]
    public void SanitizeTranslationCandidate_CollapsesRepeatedChoices_AndPrefersOriginalEquivalentTerm()
    {
        string original = "\u7814\u7A76\u8005";
        string translated = "\u7814\u7A76\u8005\u3001\u7814\u7A76\u54E1\u3001\u7814\u7A76\u8005\u3001\u7814\u7A76\u54E1\u3001\u7814\u7A76\u8005";

        string result = TranslationTextSanitizer.Sanitize(original, translated, TranslationLanguage.TraditionalChinese);

        Assert.Equal("\u7814\u7A76\u8005", result);
    }

    [Fact]
    public void SanitizeTranslationCandidate_RemovesPromptAndRomanizedGloss_ForJapaneseTargets()
    {
        string original = "\u795E\u7D1A\u7814\u7A76\u54E1\n\u91CD\u8981";
        string translated =
            "5. Translate into Japanese.\n" +
            "\u795E\u7D1A\u7814\u7A76\u54E1\n" +
            "\u91CD\u8981\u3002\n" +
            "\u795E\u7D1A\u7814\u7A76\u54E1 (Shin-kyuu kenkyuuin)\n" +
            "\u91CD\u8981 (Juuyou)";

        string result = TranslationTextSanitizer.Sanitize(original, translated, TranslationLanguage.Japanese);

        Assert.Equal(
            string.Join(Environment.NewLine, "\u795E\u7D1A\u7814\u7A76\u54E1", "\u91CD\u8981"),
            result);
    }

    [Fact]
    public void SanitizeTranslationCandidate_RemovesJapaneseAssistantReplyOutput()
    {
        string original = "\u300C\u8AD6\u58C7\u300D \u5206\u9801\u4E2D\u6C92\u6709\u4EFB\u4F55\u90F5\u4EF6\u3002";
        string translated =
            "\u306F\u3044\u3001\u627F\u77E5\u3044\u305F\u3057\u307E\u3057\u305F\u3002\n" +
            "\u300C\u30E1\u30FC\u30EB\u304C\u5C4A\u304B\u306A\u3044\u300D\u3068\u306E\u3053\u3068\u3001\u5927\u5909\u7533\u3057\u8A33\u3054\u3056\u3044\u307E\u305B\u3093\u3002\n" +
            "\u304A\u8ABF\u3079\u3044\u305F\u3057\u307E\u3059\u3002\n" +
            "\u3082\u3057\u53EF\u80FD\u3067\u3042\u308C\u3070\u3001\u53D7\u4FE1\u5074\u306E\u30E1\u30FC\u30EB\u30A2\u30C9\u30EC\u30B9\u3092\u304A\u77E5\u3089\u305B\u304F\u3060\u3055\u3044\u3002";

        string result = TranslationTextSanitizer.Sanitize(original, translated, TranslationLanguage.Japanese);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildFailureFallbackText_UsesOriginalCjkAsLastResort_ForJapaneseTarget()
    {
        string result = TranslationFallbackPolicy.BuildFailureFallbackText("\u56E0\u70BA", TranslationLanguage.Japanese);

        Assert.Equal("\u56E0\u70BA", result);
    }

    [Fact]
    public void TranslationSelectionVisibility_ShowsOcrOnlyFallback()
    {
        var converter = new GimmeCapture.Converters.TranslationSelectionVisibilityConverter();
        object? result = converter.Convert(
            ["OCR text", string.Empty, false, false, true],
            typeof(bool),
            null,
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(Assert.IsType<bool>(result));
    }

    [Fact]
    public void IsJapaneseTranslationAcceptable_AllowsChangedCjkTranslation()
    {
        bool result = TranslationFallbackPolicy.IsJapaneseAcceptable(
            "\u91CD\u8981",
            "\u91CD\u8981\u6027",
            "\u91CD\u8981",
            "\u91CD\u8981\u6027");

        Assert.True(result);
    }

    [Fact]
    public void ShouldRetryWithMinimalPrompt_RetriesUnchangedChineseEcho_ForJapaneseTarget()
    {
        bool result = LlamaSharpTranslationEngine.ShouldRetryWithMinimalPrompt(
            "\u56E0\u70BA",
            "\u56E0\u70BA",
            TranslationLanguage.Japanese);

        Assert.True(result);
    }

    [Fact]
    public void ShouldRetryWithMinimalPrompt_RetriesJapaneseAssistantReplyOutput()
    {
        bool result = LlamaSharpTranslationEngine.ShouldRetryWithMinimalPrompt(
            "\u300C\u8AD6\u58C7\u300D \u5206\u9801\u4E2D\u6C92\u6709\u4EFB\u4F55\u90F5\u4EF6\u3002",
            "\u627F\u77E5\u3044\u305F\u3057\u307E\u3057\u305F\u3002\n\u78BA\u8A8D\u3055\u305B\u3066\u3044\u305F\u3060\u304D\u307E\u3059\u3002",
            TranslationLanguage.Japanese);

        Assert.True(result);
    }

    [Fact]
    public void SanitizeRecognizedOcrText_RemovesSymbolNoise_AndHiddenCharacters()
    {
        string raw = "※※\n\u200B\u56E0\u70BA\uFEFF\n***";

        string result = OcrTextSanitizer.Sanitize(raw);

        Assert.Equal("\u56E0\u70BA", result);
    }

    [Fact]
    public void SanitizeRecognizedOcrText_NormalizesCjkTerminalMiddleDot()
    {
        string result = OcrTextSanitizer.Sanitize("\u8AD6\u58C7\u5206\u9801\u4E2D\u6C92\u6709\u4EFB\u4F55\u90F5\u4EF6\u00B7");

        Assert.Equal("\u8AD6\u58C7\u5206\u9801\u4E2D\u6C92\u6709\u4EFB\u4F55\u90F5\u4EF6\u3002", result);
    }

    [Fact]
    public void BuildTranslateGemmaPrompt_UsesNativeTurnTemplateAndLanguageCodes()
    {
        string prompt = LlamaSharpTranslationEngine.BuildTranslateGemmaPrompt(
            "\u8AD6\u58C7",
            OCRLanguage.TraditionalChinese,
            TranslationLanguage.Japanese);

        Assert.StartsWith("<start_of_turn>user\n", prompt, StringComparison.Ordinal);
        Assert.Contains("Chinese (zh-TW) to Japanese (ja)", prompt, StringComparison.Ordinal);
        Assert.EndsWith("<end_of_turn>\n<start_of_turn>model\n", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFailureFallbackText_StripsLowSignalOcrNoise_ForJapaneseTarget()
    {
        string raw = "※※\n\u56E0\u70BA\n***";

        string result = TranslationFallbackPolicy.BuildFailureFallbackText(raw, TranslationLanguage.Japanese);

        Assert.Equal("\u56E0\u70BA", result);
    }
}
