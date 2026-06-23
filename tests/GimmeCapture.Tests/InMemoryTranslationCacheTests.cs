using GimmeCapture.Models;
using GimmeCapture.Services.Translation;

namespace GimmeCapture.Tests;

public sealed class InMemoryTranslationCacheTests
{
    [Fact]
    public void TryGet_MissingKey_ReturnsFalseAndEmptyValue()
    {
        var cache = new InMemoryTranslationCache();

        bool found = cache.TryGet("missing", out string value);

        Assert.False(found);
        Assert.Equal(string.Empty, value);
    }

    [Fact]
    public void SetThenTryGet_RoundTripsStoredValue()
    {
        var cache = new InMemoryTranslationCache();

        cache.Set("key", "value");

        Assert.True(cache.TryGet("key", out string value));
        Assert.Equal("value", value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Set_EmptyOrNullKey_IsIgnored(string? key)
    {
        var cache = new InMemoryTranslationCache();

        cache.Set(key!, "value");

        Assert.False(cache.TryGet(key ?? string.Empty, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Set_EmptyOrNullValue_IsIgnored(string? value)
    {
        var cache = new InMemoryTranslationCache();

        cache.Set("key", value!);

        Assert.False(cache.TryGet("key", out _));
    }

    [Fact]
    public void Set_OverwritesExistingValueForSameKey()
    {
        var cache = new InMemoryTranslationCache();

        cache.Set("key", "first");
        cache.Set("key", "second");

        Assert.True(cache.TryGet("key", out string value));
        Assert.Equal("second", value);
    }

    [Fact]
    public void TryGet_IsCaseSensitiveOnKey()
    {
        var cache = new InMemoryTranslationCache();

        cache.Set("Key", "value");

        Assert.False(cache.TryGet("key", out _));
        Assert.True(cache.TryGet("Key", out _));
    }

    [Fact]
    public void BuildKey_EncodesAllComponents()
    {
        var cache = new InMemoryTranslationCache();

        string key = cache.BuildKey(
            TranslationEngine.LlamaSharp,
            OCRLanguage.Japanese,
            TranslationLanguage.TraditionalChinese,
            "Hello");

        Assert.Equal("LlamaSharp|Japanese|TraditionalChinese|hello", key);
    }

    [Fact]
    public void BuildKey_NormalizesTextByTrimmingAndLowercasing()
    {
        var cache = new InMemoryTranslationCache();

        string key = cache.BuildKey(
            TranslationEngine.LlamaSharp,
            OCRLanguage.English,
            TranslationLanguage.English,
            "  Mixed CASE  ");

        Assert.EndsWith("|mixed case", key, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildKey_BlankText_NormalizesToEmptyTextComponent()
    {
        var cache = new InMemoryTranslationCache();

        string key = cache.BuildKey(
            TranslationEngine.LlamaSharp,
            OCRLanguage.English,
            TranslationLanguage.English,
            "   ");

        Assert.Equal("LlamaSharp|English|English|", key);
    }

    [Fact]
    public void BuildKey_DiffersWhenLanguagePairDiffers_SameText()
    {
        var cache = new InMemoryTranslationCache();

        string toChinese = cache.BuildKey(
            TranslationEngine.LlamaSharp,
            OCRLanguage.English,
            TranslationLanguage.TraditionalChinese,
            "hello");
        string toJapanese = cache.BuildKey(
            TranslationEngine.LlamaSharp,
            OCRLanguage.English,
            TranslationLanguage.Japanese,
            "hello");

        Assert.NotEqual(toChinese, toJapanese);
    }

    [Fact]
    public void BuiltKey_CanBeUsedForSetAndGet()
    {
        var cache = new InMemoryTranslationCache();
        string key = cache.BuildKey(
            TranslationEngine.LlamaSharp,
            OCRLanguage.English,
            TranslationLanguage.TraditionalChinese,
            "Hello");

        cache.Set(key, "你好");

        Assert.True(cache.TryGet(key, out string value));
        Assert.Equal("你好", value);
    }
}
