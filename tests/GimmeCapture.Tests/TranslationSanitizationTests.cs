using GimmeCapture.Models;
using GimmeCapture.Services.Translation;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// Unit tests for the pure, stateless translation text-sanitization / acceptance helpers
/// (<see cref="TranslationService"/> *Core methods). They drive the full private helper chain
/// (gloss stripping, repetition collapse, meta/prompt detection, per-target script checks).
/// </summary>
public sealed class TranslationSanitizationTests
{
    // ---------- IsTranslationAcceptableCore ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Acceptable_EmptyOrWhitespace_IsRejected(string translated)
        => Assert.False(TranslationService.IsTranslationAcceptableCore("Hello", translated, TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Acceptable_ChineseForLatinSource_IsAccepted()
        => Assert.True(TranslationService.IsTranslationAcceptableCore("Hello", "你好世界", TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Acceptable_ChineseForJapaneseSource_IsAccepted()
        => Assert.True(TranslationService.IsTranslationAcceptableCore("こんにちは", "你好", TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Acceptable_ChineseForKoreanSource_IsAccepted()
        => Assert.True(TranslationService.IsTranslationAcceptableCore("안녕하세요", "你好", TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Acceptable_IdenticalToOriginal_IsRejected()
        => Assert.False(TranslationService.IsTranslationAcceptableCore("你好世界", "你好世界", TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Acceptable_MetaPreamble_IsRejected()
        => Assert.False(TranslationService.IsTranslationAcceptableCore("Hello", "Here is the translation: 你好", TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Acceptable_PromptEcho_IsRejected()
        => Assert.False(TranslationService.IsTranslationAcceptableCore("Hello", "User: 你好", TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Acceptable_RunawayRepetition_IsRejected()
        => Assert.False(TranslationService.IsTranslationAcceptableCore("x", "foo foo foo foo foo foo foo foo", TranslationLanguage.English));

    [Fact]
    public void Acceptable_JapaneseTargetCjkNoKana_IsAccepted()
        => Assert.True(TranslationService.IsTranslationAcceptableCore("中文", "中文字", TranslationLanguage.Japanese));

    [Theory]
    [InlineData("Hello", "Hello world", TranslationLanguage.English, true)]
    [InlineData("Hello", "12345", TranslationLanguage.English, false)]   // no Latin letters
    [InlineData("Hello", "안녕하세요", TranslationLanguage.Korean, true)]
    [InlineData("Hello", "你好", TranslationLanguage.Korean, false)]       // no Hangul
    [InlineData("Hello", "こんにちは", TranslationLanguage.Japanese, true)]
    public void Acceptable_PerTargetScriptChecks(string original, string translated, TranslationLanguage target, bool expected)
        => Assert.Equal(expected, TranslationService.IsTranslationAcceptableCore(original, translated, target));

    // ---------- SanitizeTranslationCandidateCore ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyOrWhitespace_ReturnsEmpty(string translated)
        => Assert.Equal(string.Empty, TranslationService.SanitizeTranslationCandidateCore("Hello", translated, TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Sanitize_RemovesParentheticalLatinGloss()
    {
        string result = TranslationService.SanitizeTranslationCandidateCore("Hello", "你好 (nihao)", TranslationLanguage.TraditionalChinese);
        Assert.Contains("你好", result);
        Assert.DoesNotContain("nihao", result);
    }

    [Fact]
    public void Sanitize_RemovesTrailingSlashLatinGloss()
    {
        string result = TranslationService.SanitizeTranslationCandidateCore("Hello", "你好 / nihao", TranslationLanguage.TraditionalChinese);
        Assert.Contains("你好", result);
        Assert.DoesNotContain("nihao", result);
    }

    [Fact]
    public void Sanitize_DeduplicatesIdenticalLines()
        => Assert.Equal("你好", TranslationService.SanitizeTranslationCandidateCore("Hello", "你好\n你好", TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Sanitize_KeepsCleanChinese()
    {
        string result = TranslationService.SanitizeTranslationCandidateCore("Hello world", "你好，世界", TranslationLanguage.TraditionalChinese);
        Assert.Contains("你好", result);
        Assert.Contains("世界", result);
    }

    [Fact]
    public void Sanitize_StripsKanaArtifactsForChineseTarget_KeepsChinese()
    {
        // Source is Japanese; a Chinese target should drop leaked kana but keep the Chinese.
        string result = TranslationService.SanitizeTranslationCandidateCore("こんにちは", "你好 こんにちは", TranslationLanguage.TraditionalChinese);
        Assert.Contains("你好", result);
    }

    // ---------- TryPromoteBestEffortTranslationCore ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Promote_EmptyOrWhitespace_ReturnsEmpty(string translated)
        => Assert.Equal(string.Empty, TranslationService.TryPromoteBestEffortTranslationCore("Hello", translated, TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Promote_ChineseOutput_IsReturned()
        => Assert.Equal("你好", TranslationService.TryPromoteBestEffortTranslationCore("Hello", "你好", TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Promote_NonChineseForChineseTarget_ReturnsEmpty()
        => Assert.Equal(string.Empty, TranslationService.TryPromoteBestEffortTranslationCore("Hello", "12345", TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Promote_MetaText_ReturnsEmpty()
        => Assert.Equal(string.Empty, TranslationService.TryPromoteBestEffortTranslationCore("Hello", "Here is the translation: 你好", TranslationLanguage.TraditionalChinese));

    [Fact]
    public void Promote_OverlongOutput_ReturnsEmpty()
        => Assert.Equal(string.Empty, TranslationService.TryPromoteBestEffortTranslationCore("Hi", new string('字', 120), TranslationLanguage.TraditionalChinese));

    [Theory]
    [InlineData("Hello", "World", TranslationLanguage.English, "World")]
    [InlineData("Hello", "안녕", TranslationLanguage.Korean, "안녕")]
    public void Promote_PerTarget_ReturnsTrimmed(string original, string translated, TranslationLanguage target, string expected)
        => Assert.Equal(expected, TranslationService.TryPromoteBestEffortTranslationCore(original, translated, target));

    [Fact]
    public void Promote_EnglishTargetNonLatin_ReturnsEmpty()
        => Assert.Equal(string.Empty, TranslationService.TryPromoteBestEffortTranslationCore("Hello", "你好", TranslationLanguage.English));

    // ---------- additional branch coverage ----------

    [Fact]
    public void Acceptable_SimplifiedChineseForLatinSource_IsAccepted()
        => Assert.True(TranslationService.IsTranslationAcceptableCore("Hello", "你好", TranslationLanguage.SimplifiedChinese));

    [Fact]
    public void Acceptable_JapaneseAssistantPreamble_IsRejected()
        => Assert.False(TranslationService.IsTranslationAcceptableCore("Hello", "承知いたしました。こんにちは", TranslationLanguage.Japanese));

    [Fact]
    public void Acceptable_JapaneseTargetMostlyLatinInstruction_IsRejected()
        => Assert.False(TranslationService.IsTranslationAcceptableCore("Hello", "please translate this text right now", TranslationLanguage.Japanese));

    [Fact]
    public void Acceptable_TooManyLinesForSingleLineSource_IsRejected()
        => Assert.False(TranslationService.IsTranslationAcceptableCore("one source line", "a\nb\nc\nd\ne", TranslationLanguage.English));

    [Fact]
    public void Sanitize_RemovesFullWidthParentheticalLatinGloss()
    {
        string result = TranslationService.SanitizeTranslationCandidateCore("Hello", "你好（nihao）", TranslationLanguage.TraditionalChinese);
        Assert.Contains("你好", result);
        Assert.DoesNotContain("nihao", result);
    }

    [Fact]
    public void Sanitize_StripsKoreanArtifactsForChineseTarget_KeepsChinese()
    {
        string result = TranslationService.SanitizeTranslationCandidateCore("안녕하세요", "你好 안녕하세요", TranslationLanguage.TraditionalChinese);
        Assert.Contains("你好", result);
    }

    [Fact]
    public void Sanitize_StripsBracketedKanaForChineseTarget_KeepsChinese()
    {
        string result = TranslationService.SanitizeTranslationCandidateCore("こんにちは", "你好「こんにちは」", TranslationLanguage.TraditionalChinese);
        Assert.Contains("你好", result);
    }

    [Fact]
    public void Sanitize_EnglishTarget_KeepsLatinLine()
    {
        string result = TranslationService.SanitizeTranslationCandidateCore("你好", "Hello world", TranslationLanguage.English);
        Assert.Contains("Hello", result);
    }

    [Fact]
    public void Sanitize_DropsLatinOnlyNoiseLineForChineseTarget()
    {
        // Two lines: a Chinese translation and a stray Latin gloss line; the Chinese should win.
        string result = TranslationService.SanitizeTranslationCandidateCore("Hello", "你好世界\nnihao shijie", TranslationLanguage.TraditionalChinese);
        Assert.Contains("你好", result);
        Assert.DoesNotContain("shijie", result);
    }

    [Fact]
    public void Promote_KoreanTargetNonHangul_ReturnsEmpty()
        => Assert.Equal(string.Empty, TranslationService.TryPromoteBestEffortTranslationCore("Hello", "你好", TranslationLanguage.Korean));

    [Fact]
    public void Promote_JapaneseTargetWithKana_IsReturned()
        => Assert.Equal("こんにちは", TranslationService.TryPromoteBestEffortTranslationCore("Hello", "こんにちは", TranslationLanguage.Japanese));

    [Fact]
    public void Promote_RunawayRepetition_ReturnsEmpty()
        => Assert.Equal(string.Empty, TranslationService.TryPromoteBestEffortTranslationCore("x", "foo foo foo foo foo foo foo foo", TranslationLanguage.English));

    [Fact]
    public void Sanitize_StripsTrailingKanaSourceSegmentForChineseTarget()
    {
        string result = TranslationService.SanitizeTranslationCandidateCore("こんにちは", "你好 - こんにちは", TranslationLanguage.TraditionalChinese);
        Assert.Contains("你好", result);
    }

    [Fact]
    public void Sanitize_StripsBracketedKoreanForChineseTarget_KeepsChinese()
    {
        string result = TranslationService.SanitizeTranslationCandidateCore("안녕하세요", "你好[안녕하세요]", TranslationLanguage.TraditionalChinese);
        Assert.Contains("你好", result);
    }

    [Fact]
    public void Acceptable_JapaneseTargetLongCjkSourceWithoutKana_IsRejected()
    {
        // Source is a long CJK string; a kana-less CJK candidate for a Japanese target is rejected.
        Assert.False(TranslationService.IsTranslationAcceptableCore("中文字符串很長一段", "中文翻譯結果文字", TranslationLanguage.Japanese));
    }

    [Fact]
    public void Acceptable_ChineseSourceToChinese_AcceptedWhenDifferent()
        => Assert.True(TranslationService.IsTranslationAcceptableCore("中文原文一段話", "另一段中文翻譯", TranslationLanguage.TraditionalChinese));

    [Theory]
    [InlineData("User: 你好")]
    [InlineData("Assistant: 你好")]
    [InlineData("Input: 你好")]
    [InlineData("Output: 你好")]
    [InlineData("System: 你好")]
    [InlineData("Translate the following text 你好")]
    [InlineData("Translate into Chinese 你好")]
    [InlineData("The target language is Chinese 你好")]
    [InlineData("Output ONLY the translation 你好")]
    [InlineData("No explanations, no markdown, no json 你好")]
    [InlineData("翻訳してください 你好")]
    public void Acceptable_PromptOrControlMarkers_AreRejected(string translated)
        => Assert.False(TranslationService.IsTranslationAcceptableCore("Hello", translated, TranslationLanguage.TraditionalChinese));

    [Theory]
    [InlineData("Translation result: 你好")]
    [InlineData("Translated text: 你好")]
    [InlineData("Following translation 你好")]
    [InlineData("以下是翻譯 你好")]
    [InlineData("翻譯如下 你好")]
    [InlineData("Summary: 你好")]
    [InlineData("Note: 你好")]
    public void Acceptable_MetaMarkers_AreRejected(string translated)
        => Assert.False(TranslationService.IsTranslationAcceptableCore("Hello", translated, TranslationLanguage.TraditionalChinese));
}
