using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class LlamaModelDisplayNamesTests
{
    [Theory]
    [InlineData("translategemma-4b-it", "TranslateGemma 4B")]
    [InlineData("gemma-3-4b-it-q4", "Gemma 3 4B")]
    [InlineData("translategemma-12b-it", "TranslateGemma 12B (Experimental)")]
    public void Get_KnownModelId_ReturnsDisplayName(string modelId, string expected)
    {
        Assert.Equal(expected, LlamaModelDisplayNames.Get(modelId));
    }

    [Fact]
    public void Get_UnknownModelId_ReturnsIdItself()
    {
        Assert.Equal("some-custom-model", LlamaModelDisplayNames.Get("some-custom-model"));
    }

    [Fact]
    public void Get_EmptyString_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, LlamaModelDisplayNames.Get(string.Empty));
    }
}
