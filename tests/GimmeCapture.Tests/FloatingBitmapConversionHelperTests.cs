using System;
using System.Runtime.InteropServices;
using GimmeCapture.Services.Core.AI;
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

    [Fact]
    public void ThePinsPixels_ComeOutUnpremultiplied_WhenAskedFor()
    {
        using var copy = FromPremulBgra(SKAlphaType.Unpremul, 0xFF_C8_64_32u, 0x00_00_00_00u, 0x80_64_32_19u);

        // 100, 50 and 25 at alpha 128 are 199, 100 and 50.
        Assert.Equal(new[] { 0xFF_C8_64_32u, 0x00_00_00_00u, 0x80_C7_64_32u }, Words(copy));
    }

    [Fact]
    public void ThePinsPixels_AreCopiedAsTheyAre_IntoAPremultipliedBitmap()
    {
        uint[] pixels = { 0xFF_C8_64_32u, 0x00_00_00_00u, 0x80_64_32_19u };
        using var copy = FromPremulBgra(SKAlphaType.Premul, pixels);

        Assert.Equal(pixels, Words(copy));
    }

    // A removal on a pin an earlier one left with soft edges: the pin hands its pixels over unpremultiplied, the
    // removal keeps or clears each, and the result comes back premultiplied. Handed over premultiplied, a translucent
    // pixel it kept came back premultiplied twice, 0x80643219 as 0x8032190D: darker with every removal.
    [Fact]
    public void ARemoval_HandsBackTheTranslucentPixelsItKeeps_AsThePinHadThem()
    {
        // Opaque, translucent and cleared pixels the mask keeps, and an opaque one it clears.
        using var handedOver = FromPremulBgra(SKAlphaType.Unpremul, 0xFF_C8_64_32u, 0x80_64_32_19u, 0x00_00_00_00u, 0xFF_10_20_30u);
        using var mask = new SKBitmap(4, 1, SKColorType.Gray8, SKAlphaType.Opaque);
        new byte[] { 255, 255, 255, 0 }.CopyTo(mask.GetPixelSpan());

        using var removed = BackgroundRemovalService.ApplyMask(handedOver, mask, 0f, 1f);

        Assert.Equal(new[] { 0xFF_C8_64_32u, 0x80_64_32_19u, 0u, 0u }, CopyAsPremulBgra(removed));
    }

    private static uint[] CopyAsPremulBgra(SKColorType colorType, SKAlphaType alphaType, params uint[] pixels)
    {
        using var source = new SKBitmap(pixels.Length, 1, colorType, alphaType);
        pixels.AsSpan().CopyTo(MemoryMarshal.Cast<byte, uint>(source.GetPixelSpan()));
        return CopyAsPremulBgra(source);
    }

    private static unsafe uint[] CopyAsPremulBgra(SKBitmap source)
    {
        var converted = new uint[source.Width * source.Height];
        fixed (uint* destination = converted)
        {
            bool copied = FloatingBitmapConversionHelper.TryCopyAsPremulBgra(
                source, (IntPtr)destination, source.Width * sizeof(uint), source.Height, out string? error);
            Assert.True(copied, error);
        }

        return converted;
    }

    // A one-row bitmap of alphaType holding the premultiplied pixels, as TryCopyToSkBitmap copies a pin's.
    private static unsafe SKBitmap FromPremulBgra(SKAlphaType alphaType, params uint[] pixels)
    {
        var bitmap = new SKBitmap(pixels.Length, 1, SKColorType.Bgra8888, alphaType);
        fixed (uint* source = pixels)
        {
            bool copied = FloatingBitmapConversionHelper.TryCopyFromPremulBgra(
                (IntPtr)source, pixels.Length * sizeof(uint), bitmap, out string? error);
            Assert.True(copied, error);
        }

        return bitmap;
    }

    private static uint[] Words(SKBitmap bitmap) => MemoryMarshal.Cast<byte, uint>(bitmap.GetPixelSpan()).ToArray();
}
