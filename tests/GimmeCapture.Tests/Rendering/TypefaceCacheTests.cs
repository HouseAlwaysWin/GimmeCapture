using GimmeCapture.Services.Core.Rendering;
using SkiaSharp;

namespace GimmeCapture.Tests;

public class TypefaceCacheTests
{
    [Fact]
    public void Get_ReturnsSameCachedInstanceForSameKey()
    {
        var a = TypefaceCache.Get("Arial", SKFontStyleWeight.Bold, SKFontStyleSlant.Italic);
        var b = TypefaceCache.Get("Arial", SKFontStyleWeight.Bold, SKFontStyleSlant.Italic);
        Assert.Same(a, b);
    }

    [Fact]
    public void Get_NeverReturnsNull_EvenForUnknownFamily()
    {
        Assert.NotNull(TypefaceCache.Get("definitely-not-a-real-font-family-xyz"));
    }
}
