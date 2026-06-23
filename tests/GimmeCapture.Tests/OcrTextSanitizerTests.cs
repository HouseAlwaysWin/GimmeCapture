using GimmeCapture.Services.Translation;

namespace GimmeCapture.Tests;

public class OcrTextSanitizerTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyOrWhitespace_ReturnsEmpty(string input)
    {
        Assert.Equal(string.Empty, OcrTextSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_CollapsesInternalWhitespace()
    {
        Assert.Equal("a b", OcrTextSanitizer.Sanitize("a    b"));
    }

    [Fact]
    public void Sanitize_RemovesZeroWidthCharacters()
    {
        Assert.Equal("hello", OcrTextSanitizer.Sanitize("he\u200Bllo"));
    }

    [Fact]
    public void Sanitize_DropsLowSignalLines_ButKeepsMeaningfulOnes()
    {
        string result = OcrTextSanitizer.Sanitize("ok\n::");
        Assert.Equal("ok", result);
    }

    [Fact]
    public void Sanitize_DeduplicatesLinesIgnoringCaseAndPunctuation()
    {
        // All three normalize to "hello"; only the first occurrence is kept.
        string result = OcrTextSanitizer.Sanitize("Hello!\nhello\nHELLO.");
        Assert.Equal("Hello!", result);
    }

    [Fact]
    public void Sanitize_NormalizesCjkTrailingMiddleDotToIdeographicPeriod()
    {
        // Middle dot (U+00B7) after CJK text becomes the ideographic full stop (U+3002).
        string result = OcrTextSanitizer.Sanitize("中文·");
        Assert.Equal("中文。", result);
    }
}
