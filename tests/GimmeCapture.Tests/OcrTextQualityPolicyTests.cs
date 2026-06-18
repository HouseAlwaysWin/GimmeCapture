using GimmeCapture.Services.Translation;

namespace GimmeCapture.Tests;

public class OcrTextQualityPolicyTests
{
    [Theory]
    [InlineData("hello", 0.9f, true)]
    [InlineData("\u56E0\u70BA", 0.3f, true)]
    [InlineData("\u56E0\u70BA", 0.2f, false)]
    [InlineData("***", 0.9f, false)]
    [InlineData("hello\u200B", 0.9f, false)]
    [InlineData("hello", 0.05f, false)]
    public void IsUseful_CharacterizesOcrQualityRules(string text, float confidence, bool expected)
    {
        Assert.Equal(expected, OcrTextQualityPolicy.IsUseful(text, confidence));
    }
}
