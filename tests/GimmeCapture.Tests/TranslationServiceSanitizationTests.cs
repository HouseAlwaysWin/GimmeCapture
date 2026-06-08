using System.Reflection;

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
        string result = InvokeSanitizeTranslationCandidate(original, translated, TranslationLanguage.TraditionalChinese);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CleanupTranslationResult_CollapsesRepeatedTokenCycles()
    {
        string raw = "\u7814\u7A76\u8005\u3001\u7814\u7A76\u54E1\u3001\u7814\u7A76\u8005\u3001\u7814\u7A76\u54E1\u3001\u7814\u7A76\u8005\u3001\u7814\u7A76\u54E1";

        string result = InvokeLlamaCleanupTranslationResult(raw);

        Assert.Equal("\u7814\u7A76\u8005\u3001\u7814\u7A76\u54E1", result);
    }

    [Fact]
    public void SanitizeTranslationCandidate_CollapsesRepeatedChoices_AndPrefersOriginalEquivalentTerm()
    {
        string original = "\u7814\u7A76\u8005";
        string translated = "\u7814\u7A76\u8005\u3001\u7814\u7A76\u54E1\u3001\u7814\u7A76\u8005\u3001\u7814\u7A76\u54E1\u3001\u7814\u7A76\u8005";

        string result = InvokeSanitizeTranslationCandidate(original, translated, TranslationLanguage.TraditionalChinese);

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

        string result = InvokeSanitizeTranslationCandidate(original, translated, TranslationLanguage.Japanese);

        Assert.Equal("\u795E\u7D1A\u7814\u7A76\u54E1\n\u91CD\u8981", result);
    }

    [Fact]
    public void BuildFailureFallbackText_UsesOriginalCjkAsLastResort_ForJapaneseTarget()
    {
        string result = InvokeBuildFailureFallbackText("\u56E0\u70BA", TranslationLanguage.Japanese);

        Assert.Equal("\u56E0\u70BA", result);
    }

    [Fact]
    public void IsJapaneseTranslationAcceptable_AllowsChangedCjkTranslation()
    {
        bool result = InvokeIsJapaneseTranslationAcceptable(
            "\u91CD\u8981",
            "\u91CD\u8981\u6027",
            "\u91CD\u8981",
            "\u91CD\u8981\u6027");

        Assert.True(result);
    }

    [Fact]
    public void ShouldRetryWithMinimalPrompt_RetriesUnchangedChineseEcho_ForJapaneseTarget()
    {
        bool result = InvokeShouldRetryWithMinimalPrompt(
            "\u56E0\u70BA",
            "\u56E0\u70BA",
            TranslationLanguage.Japanese);

        Assert.True(result);
    }

    private static string InvokeSanitizeTranslationCandidate(string original, string translated, TranslationLanguage target)
    {
        var method = typeof(TranslationService).GetMethod(
            "SanitizeTranslationCandidate",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return Assert.IsType<string>(method!.Invoke(null, [original, translated, target]));
    }

    private static string InvokeLlamaCleanupTranslationResult(string raw)
    {
        var method = typeof(LlamaSharpTranslationEngine).GetMethod(
            "CleanupTranslationResult",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return Assert.IsType<string>(method!.Invoke(null, [raw]));
    }

    private static string InvokeBuildFailureFallbackText(string original, TranslationLanguage target)
    {
        var method = typeof(TranslationService).GetMethod(
            "BuildFailureFallbackText",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return Assert.IsType<string>(method!.Invoke(null, [original, target]));
    }

    private static bool InvokeIsJapaneseTranslationAcceptable(
        string original,
        string translated,
        string normalizedOriginal,
        string normalizedTranslated)
    {
        var method = typeof(TranslationService).GetMethod(
            "IsJapaneseTranslationAcceptable",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return Assert.IsType<bool>(method!.Invoke(null, [original, translated, normalizedOriginal, normalizedTranslated]));
    }

    private static bool InvokeShouldRetryWithMinimalPrompt(
        string source,
        string translated,
        TranslationLanguage target)
    {
        var method = typeof(LlamaSharpTranslationEngine).GetMethod(
            "ShouldRetryWithMinimalPrompt",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        return Assert.IsType<bool>(method!.Invoke(null, [source, translated, target]));
    }
}
