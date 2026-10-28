using System;
using System.Runtime.InteropServices;
using GimmeCapture.ViewModels.Floating;
using SkiaSharp;
using Xunit;

namespace GimmeCapture.Tests;

// Background removal hands a pin unpremultiplied pixels, and those it cleared keep their colour at alpha 0. The pin's
// Avalonia bitmap is premultiplied, so the hand-off has to convert them: copied byte for byte, the cleared background
// was drawn added to what lies behind (a white wash over the pin's checkerboard) instead of hidden.
// Pixels are written as 0xAARRGGBB words, which is BGRA in memory.
public class FloatingBitmapConversionHelperTests
{
    [Fact]
    public void UnpremultipliedPixels_ComeOutPremultiplied()
    {
        // Opaque, cleared with its colour kept, half transparent.
        uint[] converted = CopyAsPremulBgra(SKColorType.Bgra8888, SKAlphaType.Unpremul, 0xFF_C8_64_32u, 0x00_C8_64_32u, 0x80_C8_64_32u);

        // 200, 100 and 50 at alpha 128 are 100, 50 and 25.
        Assert.Equal(new[] { 0xFF_C8_64_32u, 0x00_00_00_00u, 0x80_64_32_19u }, converted);
    }

    [Fact]
    public void PremultipliedPixels_AreCopiedAsTheyAre()
    {
        // The pin's own pixels come back this way after a crop or a rotation: premultiplied again, the translucent one
        // would darken each time.
        uint[] pixels = { 0xFF_C8_64_32u, 0x00_00_00_00u, 0x80_64_32_19u };

        Assert.Equal(pixels, CopyAsPremulBgra(SKColorType.Bgra8888, SKAlphaType.Premul, pixels));
    }

    [Fact]
    public void RgbaPixels_ComeOutInBgraOrder()
    {
        // RGBA in memory: the word reads 0xAABBGGRR.
        uint[] converted = CopyAsPremulBgra(SKColorType.Rgba8888, SKAlphaType.Premul, 0xFF_32_64_C8u);

        Assert.Equal(new[] { 0xFF_C8_64_32u }, converted);
    }

    private static unsafe uint[] CopyAsPremulBgra(SKColorType colorType, SKAlphaType alphaType, params uint[] pixels)
    {
        using var source = new SKBitmap(pixels.Length, 1, colorType, alphaType);
        pixels.AsSpan().CopyTo(MemoryMarshal.Cast<byte, uint>(source.GetPixelSpan()));

        var converted = new uint[pixels.Length];
        fixed (uint* destination = converted)
        {
            bool copied = FloatingBitmapConversionHelper.TryCopyAsPremulBgra(
                source, (IntPtr)destination, converted.Length * sizeof(uint), 1, out string? error);
            Assert.True(copied, error);
        }

        return converted;
    }
}
