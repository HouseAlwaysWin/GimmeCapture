using GimmeCapture.Services.Translation;

namespace GimmeCapture.Tests;

public class OcrTextQualityBoundaryTests
{
    [Theory]
    [InlineData("abc", 0.10f, true)]   // confidence exactly at the 0.10 gate, 3 useful chars
    [InlineData("abc", 0.09f, false)]  // just below the confidence gate
    [InlineData("ab", 0.30f, true)]    // 2 useful chars rescued by confidence >= 0.30
    [InlineData("ab", 0.29f, false)]   // 2 useful chars, confidence just below rescue
    [InlineData("a?", 0.30f, true)]    // 1 useful + punctuation placeholder, rescued by confidence
    public void IsUseful_RespectsConfidenceAndUsefulCountBoundaries(string text, float confidence, bool expected)
    {
        Assert.Equal(expected, OcrTextQualityPolicy.IsUseful(text, confidence));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void IsUseful_RejectsEmptyOrWhitespace(string text)
    {
        Assert.False(OcrTextQualityPolicy.IsUseful(text, 0.9f));
    }
}
