using GimmeCapture.ViewModels.Floating;
using SkiaSharp;

namespace GimmeCapture.Tests;

// Holds smart selection's two pixel passes to the per-pixel rules of the loops they replaced in
// FloatingImageViewModel: the preview colours every mask pixel above 127, and the confirm removes the pin's pixels the
// mask selects (the ones it does not, when keeping the selection) and copies the rest, reading a mask of another size
// at (x * maskWidth / width, y * maskHeight / height) rounded down. Both now run in bands of rows, so every image here
// is tall enough for several bands, and padded rows check that rows are found by their stride.
public class InteractiveMaskPixelsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyToImage_RemovesWhatTheMaskSelects_AndCopiesTheRest(bool keepSelected)
    {
        // Every mask value (the column) against every source alpha (the row, four times over), colour everywhere.
        const int width = 256;
        const int height = 1024;
        using var source = TestBitmaps.Create(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, (x, y) => SourcePixel(x, y));
        using var mask = TestBitmaps.Create(width, height, SKColorType.Gray8, SKAlphaType.Opaque, (x, _) => (byte)x);

        using var result = InteractiveMaskPixels.ApplyToImage(source, mask, keepSelected);

        // Premultiplied like the pin, and a removed pixel all zero: colour kept at alpha 0 would show.
        Assert.Equal(SKColorType.Bgra8888, result.ColorType);
        Assert.Equal(SKAlphaType.Premul, result.AlphaType);
        var expected = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool removed = (x > 127) != keepSelected;
                expected[(y * width) + x] = removed ? 0u : SourcePixel(x, y);
            }
        }

        Assert.Equal(expected, TestBitmaps.ReadPixels<uint>(result));
    }

    [Theory]
    [InlineData(150, 70)]
    [InlineData(700, 2500)]
    [InlineData(1, 1)]
    [InlineData(301, 5)]
    [InlineData(2, 1201)]
    public void ApplyToImage_StretchesAMaskOfAnotherSizeOverThePin(int maskWidth, int maskHeight)
    {
        const int width = 301;
        const int height = 1201;
        using var source = TestBitmaps.Create(width, height, SKColorType.Bgra8888, SKAlphaType.Premul, (x, y) => SourcePixel(x, y), padBytes: 20);
        using var mask = TestBitmaps.Create(maskWidth, maskHeight, SKColorType.Gray8, SKAlphaType.Opaque, (x, y) => (byte)TestBitmaps.Noise(x, y, 1), padBytes: 3);

        using var result = InteractiveMaskPixels.ApplyToImage(source, mask, keepSelected: false);

        byte[] maskValues = TestBitmaps.ReadPixels<byte>(mask);
        var expected = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            int maskY = Math.Clamp((int)(y * ((double)maskHeight / height)), 0, maskHeight - 1);
            for (int x = 0; x < width; x++)
            {
                int maskX = Math.Clamp((int)(x * ((double)maskWidth / width)), 0, maskWidth - 1);
                bool removed = maskValues[(maskY * maskWidth) + maskX] > 127;
                expected[(y * width) + x] = removed ? 0u : SourcePixel(x, y);
            }
        }

        Assert.Equal(expected, TestBitmaps.ReadPixels<uint>(result));
    }

    [Fact]
    public void CreateOverlay_ColoursEveryMaskValueAbove127()
    {
        // Rows of every value, shifted along each row so every column sees each value; rows padded.
        const int width = 256;
        const int height = 700;
        using var mask = TestBitmaps.Create(width, height, SKColorType.Gray8, SKAlphaType.Opaque, (x, y) => (byte)(x + y), padBytes: 9);
        var red = new SKColor(255, 80, 80, 150);

        using var overlay = InteractiveMaskPixels.CreateOverlay(mask, red);

        // Declared as what it holds, unpremultiplied BGRA, so the markers drawn on it and the hand-off to Avalonia
        // read it right. A BGRA pixel read as one little-endian word is 0xAARRGGBB.
        Assert.Equal(SKColorType.Bgra8888, overlay.ColorType);
        Assert.Equal(SKAlphaType.Unpremul, overlay.AlphaType);
        const uint redWord = 0x96FF5050u;
        var expected = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                expected[(y * width) + x] = (byte)(x + y) > 127 ? redWord : 0u;
            }
        }

        Assert.Equal(expected, TestBitmaps.ReadPixels<uint>(overlay));
    }

    // A different colour in every pixel and every alpha (by row), so a kept or removed pixel cannot pass by accident.
    private static uint SourcePixel(int x, int y) => ((uint)(y & 0xFF) << 24) | (TestBitmaps.Noise(x, y) & 0x00FFFFFFu);
}
