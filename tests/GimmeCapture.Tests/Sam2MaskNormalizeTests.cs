using GimmeCapture.Services.Core.AI;
using SkiaSharp;

namespace GimmeCapture.Tests;

// Guards SAM2Service.NormalizeToGray8, which folds the mask a click resized to the pin (the platform's 32-bit colour
// type) down to the Gray8 smart selection reads a byte at a time. It reads the grey level from one channel, so every
// byte of every pixel here differs: the wrong channel, pixel or row (padded rows: the wrong stride) cannot pass.
public class Sam2MaskNormalizeTests
{
    [Theory]
    [InlineData(SKColorType.Bgra8888, 0)]
    [InlineData(SKColorType.Rgba8888, 2)]
    public void A32BitMask_GivesItsBlueChannel(SKColorType colorType, int blueByte)
    {
        const int width = 37;
        const int height = 11;
        using var source = TestBitmaps.Create(width, height, colorType, SKAlphaType.Premul, (x, y) => TestBitmaps.Noise(x, y), padBytes: 12);

        using var gray = SAM2Service.NormalizeToGray8(source);

        Assert.Equal(SKColorType.Gray8, gray.ColorType);
        var expected = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                expected[(y * width) + x] = (byte)(TestBitmaps.Noise(x, y) >> (8 * blueByte));
            }
        }

        Assert.Equal(expected, TestBitmaps.ReadPixels<byte>(gray));
    }

    [Fact]
    public void AGray8Mask_IsCopied()
    {
        using var source = TestBitmaps.Create(29, 7, SKColorType.Gray8, SKAlphaType.Opaque, (x, y) => (byte)TestBitmaps.Noise(x, y), padBytes: 5);

        using var gray = SAM2Service.NormalizeToGray8(source);

        Assert.NotSame(source, gray);
        Assert.Equal(TestBitmaps.ReadPixels<byte>(source), TestBitmaps.ReadPixels<byte>(gray));
    }

    [Fact]
    public void AnyOtherColourType_GivesTheRedOfEachPixel()
    {
        using var source = TestBitmaps.Create(23, 9, SKColorType.Rgb565, SKAlphaType.Opaque, (x, y) => (ushort)TestBitmaps.Noise(x, y), padBytes: 6);

        using var gray = SAM2Service.NormalizeToGray8(source);

        var expected = new byte[23 * 9];
        for (int y = 0; y < 9; y++)
        {
            for (int x = 0; x < 23; x++)
            {
                expected[(y * 23) + x] = source.GetPixel(x, y).Red;
            }
        }

        Assert.Equal(expected, TestBitmaps.ReadPixels<byte>(gray));
    }
}
