using GimmeCapture.Services.OCR;
using SkiaSharp;

namespace GimmeCapture.Tests;

// Guards the two luminance passes PaddleOCREngine makes over each text box: AnalyzeLuminanceAndContrast (which decides
// whether a box is inverted and contrast-stretched) and EnhanceBitmapForOcr (the stretch itself, in place). Both are
// held to the per-pixel formulas, in the same float arithmetic, on bitmaps with padded rows: they step through rows by
// the stride they read once, and a wrong one reads the padding or the wrong row.
public class PaddleOcrLuminanceTests
{
    private const int Width = 37;
    private const int Height = 9;
    private const int PadBytes = 20;

    [Fact]
    public void AnalyzeLuminanceAndContrast_GivesTheTruncatedAverageAndTheSpread()
    {
        using var bitmap = TestBitmaps.Create(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul, Pixel, PadBytes);

        PaddleOCREngine.AnalyzeLuminanceAndContrast(bitmap, out float average, out int contrast);

        long total = 0;
        byte min = byte.MaxValue;
        byte max = byte.MinValue;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                float luminance = Luminance(Pixel(x, y));
                total += (long)luminance;
                byte luminanceByte = (byte)Math.Clamp(luminance, 0f, 255f);
                min = Math.Min(min, luminanceByte);
                max = Math.Max(max, luminanceByte);
            }
        }

        Assert.Equal(total / (float)(Width * Height), average);
        Assert.Equal(max - min, contrast);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnhanceBitmapForOcr_StretchesTheLuminanceToFullRange_AsOpaqueGrey(bool lowContrast)
    {
        // A low-contrast box is what the stretch is for: a spread of a few levels becomes 0-255.
        Func<int, int, uint> pixel = lowContrast ? (x, y) => 0xFF000000u | ((uint)(0x40 + ((x + y) % 7)) * 0x010101u) : Pixel;
        using var bitmap = TestBitmaps.Create(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul, pixel, PadBytes);

        PaddleOCREngine.EnhanceBitmapForOcr(bitmap);

        byte min = byte.MaxValue;
        byte max = byte.MinValue;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                byte luminance = (byte)Math.Clamp(Luminance(pixel(x, y)), 0f, 255f);
                min = Math.Min(min, luminance);
                max = Math.Max(max, luminance);
            }
        }

        float range = Math.Max(1f, max - min);
        var expected = new uint[Width * Height];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                float normalized = ((Luminance(pixel(x, y)) - min) / range) * 255f;
                uint value = (byte)Math.Clamp(MathF.Round(normalized), 0f, 255f);
                expected[(y * Width) + x] = 0xFF000000u | (value * 0x010101u);
            }
        }

        Assert.Equal(expected, TestBitmaps.ReadPixels<uint>(bitmap));
        Assert.Equal(255u, expected.Max(p => p & 0xFFu)); // the brightest pixel is stretched to white

        // The padding after each row is not the bitmap's to write.
        ReadOnlySpan<byte> bytes = bitmap.GetPixelSpan();
        for (int y = 0; y < Height - 1; y++)
        {
            Assert.All(bytes.Slice((y * bitmap.RowBytes) + (Width * 4), PadBytes).ToArray(), b => Assert.Equal(TestBitmaps.Padding, b));
        }
    }

    // Opaque, with a different colour at every pixel: the passes read B, G, R from bytes 0, 1, 2.
    private static uint Pixel(int x, int y) => 0xFF000000u | (TestBitmaps.Noise(x, y) & 0x00FFFFFFu);

    private static float Luminance(uint pixel)
    {
        byte blue = (byte)pixel;
        byte green = (byte)(pixel >> 8);
        byte red = (byte)(pixel >> 16);
        return (red * 0.2126f) + (green * 0.7152f) + (blue * 0.0722f);
    }
}
