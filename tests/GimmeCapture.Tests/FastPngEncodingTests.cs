using System;
using GimmeCapture.ViewModels.Floating;
using SkiaSharp;
using Xunit;

namespace GimmeCapture.Tests;

// The clipboard and background removal encode PNG with fast settings (zlib level 1, Sub filter): a 4K screenshot
// took 0.33 s instead of 2.3 s. Those bytes are what gets pasted, so they must still be exact.
public class FastPngEncodingTests
{
    private static SKBitmap Decode(SKBitmap source)
    {
        using var data = FloatingBitmapConversionHelper.EncodeFastPng(source);
        Assert.NotNull(data);
        return SKBitmap.Decode(data);
    }

    [Fact]
    public void DecodesBackToTheSamePixels()
    {
        using var source = new SKBitmap(64, 48, SKColorType.Bgra8888, SKAlphaType.Premul);
        var random = new Random(7);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            }
        }

        using var decoded = Decode(source);

        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Assert.Equal(source.GetPixel(x, y), decoded.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void KeepsTransparency()
    {
        using var source = new SKBitmap(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul);
        source.Erase(SKColors.Transparent);
        source.SetPixel(3, 3, SKColors.Red);

        using var decoded = Decode(source);

        Assert.Equal(0, decoded.GetPixel(0, 0).Alpha);
        Assert.Equal(SKColors.Red, decoded.GetPixel(3, 3));
    }
}
