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
}
