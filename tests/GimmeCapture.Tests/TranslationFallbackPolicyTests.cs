using GimmeCapture.Models;
using GimmeCapture.Services.Translation;

namespace GimmeCapture.Tests;

public class TranslationFallbackPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]   // no meaningful (letter/digit/CJK/kana/hangul) characters
    public void BuildFailureFallbackText_NoMeaningfulContent_ReturnsEmpty(string original)
    {
        Assert.Equal(string.Empty, TranslationFallbackPolicy.BuildFailureFallbackText(original, TranslationLanguage.TraditionalChinese));
    }

    [Fact]
    public void BuildFailureFallbackText_PreservesSingleMeaningfulLine()
    {
        Assert.Equal("研究者", TranslationFallbackPolicy.BuildFailureFallbackText("研究者", TranslationLanguage.TraditionalChinese));
    }

    [Fact]
    public void BuildFailureFallbackText_DeduplicatesLinesIgnoringCase()
    {
        // "Hello", "hello", "HELLO" all normalize identically; first kept.
        string result = TranslationFallbackPolicy.BuildFailureFallbackText("Hello\nhello\nHELLO", TranslationLanguage.TraditionalChinese);
        Assert.Equal("Hello", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsAcceptable_BlankTranslation_ReturnsFalse(string translated)
    {
        Assert.False(TranslationFallbackPolicy.IsAcceptable("こんにちは", translated, TranslationLanguage.English));
    }

    [Fact]
    public void IsAcceptable_EnglishTarget_AcceptsLatinTranslation()
    {
        Assert.True(TranslationFallbackPolicy.IsAcceptable("こんにちは", "Hello", TranslationLanguage.English));
    }

    [Fact]
    public void IsAcceptable_EnglishTarget_RejectsTranslationWithoutLatinLetters()
    {
        // CJK output for an English target has no Latin letter -> rejected.
        Assert.False(TranslationFallbackPolicy.IsAcceptable("こんにちは", "你好世界", TranslationLanguage.English));
    }

    [Fact]
    public void IsAcceptable_KoreanTarget_AcceptsHangulTranslation()
    {
        Assert.True(TranslationFallbackPolicy.IsAcceptable("Hello", "안녕하세요", TranslationLanguage.Korean));
    }

    [Fact]
    public void IsAcceptable_KoreanTarget_RejectsTranslationWithoutHangul()
    {
        Assert.False(TranslationFallbackPolicy.IsAcceptable("Hello", "Bonjour", TranslationLanguage.Korean));
    }

    [Fact]
    public void IsAcceptable_RejectsTranslationIdenticalToOriginal()
    {
        // Normalized translated == normalized original and length >= 4 -> rejected.
        Assert.False(TranslationFallbackPolicy.IsAcceptable("Hello", "Hello", TranslationLanguage.English));
    }

    [Fact]
    public void IsAcceptable_RejectsObviousPromptText()
    {
        Assert.False(TranslationFallbackPolicy.IsAcceptable("foo", "Translate into Japanese.", TranslationLanguage.Japanese));
    }
}
