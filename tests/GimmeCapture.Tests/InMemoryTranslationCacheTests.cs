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

    [Fact]
    public void Set_RewritingAKey_KeepsOnePlaceInTheEvictionOrder()
    {
        var cache = new InMemoryTranslationCache();

        for (int i = 0; i < 5000; i++)
        {
            cache.Set("key", $"value {i}");
        }

        // The order used to gain a place per write, so a cache that never filled up grew without bound.
        Assert.Equal(1, cache.OrderCountForTest);
        Assert.True(cache.TryGet("key", out string value));
        Assert.Equal("value 4999", value);
    }

    [Fact]
    public void Set_PastCapacity_EvictsTheEntryWrittenLongestAgo()
    {
        var cache = new InMemoryTranslationCache(maxEntries: 3, () => DateTime.UtcNow);
        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3");

        cache.Set("d", "4");

        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("d", out _));
        Assert.Equal(3, cache.OrderCountForTest);
    }

    [Fact]
    public void Set_RewritingAKey_MovesItBehindEveryOtherEntry()
    {
        var cache = new InMemoryTranslationCache(maxEntries: 3, () => DateTime.UtcNow);
        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3");

        cache.Set("a", "1 again");
        cache.Set("d", "4");

        // "b" is the oldest write now. The old queue still held "a"'s first place, and evicted the value just written.
        Assert.True(cache.TryGet("a", out string rewritten));
        Assert.Equal("1 again", rewritten);
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.True(cache.TryGet("d", out _));
    }

    [Fact]
    public void TryGet_ExpiredEntry_LeavesNoPlaceBehindToEvictItsRewriteEarly()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var cache = new InMemoryTranslationCache(maxEntries: 2, () => now);
        cache.Set("a", "1");
        now += TimeSpan.FromMinutes(11);

        Assert.False(cache.TryGet("a", out _));
        Assert.Equal(0, cache.OrderCountForTest);

        cache.Set("b", "2");
        cache.Set("a", "1 again");
        cache.Set("c", "3");

        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out string rewritten));
        Assert.Equal("1 again", rewritten);
        Assert.True(cache.TryGet("c", out _));
    }
}
