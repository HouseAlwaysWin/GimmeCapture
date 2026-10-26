using System;

using GimmeCapture.Services.Core.AI;
using SkiaSharp;

namespace GimmeCapture.Tests;

// Guards SAM2Service.FillEncoderInput, which fills the encoder's 3 x 1024 x 1024 input. Since that buffer moved to
// ArrayPool it arrives UNCLEARED, so "the fill writes every entry" stopped being a nicety and became the only thing
// keeping the previous image's floats out of the encoder. A skipped entry would not crash or fail loudly: it would
// produce a subtly wrong mask, and only when the pool happened to hand back a used array. These tests seed the
// buffer with NaN, which no real pixel produces, so a single unwritten entry is caught.
public class Sam2EncoderInputFillTests
{
    private const int Size = SAM2Service.Sam2InputSize;
    private const int Plane = Size * Size;
    private const int Length = 3 * Plane;

    // Longer than the fill needs, the way a rented array is (Rent rounds 3,145,728 up to 4,194,304), so the
    // tail can show whether the fill wrote past its input.
    private static float[] PoisonedBuffer()
    {
        var buffer = new float[Length + 1024];
        Array.Fill(buffer, float.NaN);
        return buffer;
    }

    // Exactly Size square: the fast path reads rows through a raw pointer, so a smaller bitmap would be an
    // out-of-bounds read rather than a failed assertion.
    private static SKBitmap Solid(SKColorType type, SKColor color)
    {
        var alpha = type == SKColorType.Gray8 ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var bitmap = new SKBitmap(Size, Size, type, alpha);
        bitmap.Erase(color);
        return bitmap;
    }

    [Theory]
    [InlineData(SKColorType.Bgra8888)] // fast path
    [InlineData(SKColorType.Rgba8888)] // fast path, bytes read in the other order
    [InlineData(SKColorType.Gray8)]    // GetPixel fallback
    public void WritesEveryEntry_AndNothingPastThem(SKColorType type)
    {
        using var bitmap = Solid(type, new SKColor(10, 20, 30));
        var buffer = PoisonedBuffer();

        SAM2Service.FillEncoderInput(bitmap, buffer);

        for (int i = 0; i < Length; i++)
        {
            if (float.IsNaN(buffer[i]))
            {
                Assert.Fail($"Entry {i} (plane {i / Plane}) was never written, so the pooled buffer's old value would reach the encoder.");
            }
        }

        for (int i = Length; i < buffer.Length; i++)
        {
            Assert.True(float.IsNaN(buffer[i]), $"Entry {i} lies past the encoder input and was overwritten.");
        }
    }

    [Theory]
    [InlineData(SKColorType.Bgra8888)]
    [InlineData(SKColorType.Rgba8888)]
    public void PlanesAreBlueGreenRed_InBothByteOrders(SKColorType type)
    {
        // A different value in every channel, so a swapped plane or a misread byte order cannot pass.
        using var bitmap = Solid(type, new SKColor(255, 102, 0)); // R 255, G 102, B 0
        var buffer = PoisonedBuffer();

        SAM2Service.FillEncoderInput(bitmap, buffer);

        int probe = (512 * Size) + 512; // any pixel will do: the bitmap is one colour
        Assert.Equal(0d, buffer[probe], 4);                    // plane 0: blue
        Assert.Equal(102d / 255d, buffer[Plane + probe], 4);   // plane 1: green
        Assert.Equal(1d, buffer[(2 * Plane) + probe], 4);      // plane 2: red
    }
}
