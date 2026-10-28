using System;
using System.Runtime.InteropServices;
using Avalonia;
using GimmeCapture.ViewModels.Shared;
using SkiaSharp;

namespace GimmeCapture.Tests;

// The crop + clockwise turn the compress editor and compare view apply while drawing a decoded frame into their reused
// preview bitmap. It replaced FloatingBitmapConversionHelper.TransformBitmap on that path, so it must turn the same way.
public class VideoFrameBitmapTests
{
    // A 3×2 frame whose pixels count 1, 2, 3 along the top row and 11, 12, 13 along the bottom one.
    private static readonly uint[] Frame3x2 = { 1, 2, 3, 11, 12, 13 };

    private static uint[] Turn(uint[] frame, int width, PixelRect region, int rotation, int rowPadding = 0)
    {
        PixelSize size = VideoFrameBitmap.OutputSize(region, rotation);
        int rowBytes = (size.Width * 4) + rowPadding;
        var destination = new byte[rowBytes * size.Height];
        VideoFrameBitmap.CopyRotated(MemoryMarshal.AsBytes(frame.AsSpan()), width, region, rotation, destination, rowBytes);

        var pixels = new uint[size.Width * size.Height];
        for (int y = 0; y < size.Height; y++)
        {
            MemoryMarshal.Cast<byte, uint>(destination.AsSpan(y * rowBytes, size.Width * 4)).CopyTo(pixels.AsSpan(y * size.Width));
        }

        return pixels;
    }

    [Theory]
    [InlineData(0, 3, 2)]
    [InlineData(90, 2, 3)]
    [InlineData(180, 3, 2)]
    [InlineData(270, 2, 3)]
    public void OutputSize_SwapsTheSidesForAQuarterTurn(int rotation, int width, int height)
    {
        Assert.Equal(new PixelSize(width, height), VideoFrameBitmap.OutputSize(new PixelRect(0, 0, 3, 2), rotation));
    }

    [Fact]
    public void NoTurn_CopiesTheFrame()
    {
        Assert.Equal(Frame3x2, Turn(Frame3x2, 3, new PixelRect(0, 0, 3, 2), 0));
    }

    [Fact]
    public void QuarterTurn_IsClockwise()
    {
        // The left column, read from the bottom up, becomes the top row.
        Assert.Equal(new uint[] { 11, 1, 12, 2, 13, 3 }, Turn(Frame3x2, 3, new PixelRect(0, 0, 3, 2), 90));
    }

    [Fact]
    public void HalfTurn_ReversesRowsAndColumns()
    {
        Assert.Equal(new uint[] { 13, 12, 11, 3, 2, 1 }, Turn(Frame3x2, 3, new PixelRect(0, 0, 3, 2), 180));
    }

    [Fact]
    public void ThreeQuarterTurn_IsAnticlockwiseQuarter()
    {
        // The right column, read from the top down, becomes the top row.
        Assert.Equal(new uint[] { 3, 13, 2, 12, 1, 11 }, Turn(Frame3x2, 3, new PixelRect(0, 0, 3, 2), 270));
    }

    [Fact]
    public void Crop_IsTakenBeforeTheTurn()
    {
        // Columns 1–2 of the frame (2, 3 / 12, 13), then a quarter turn.
        Assert.Equal(new uint[] { 12, 2, 13, 3 }, Turn(Frame3x2, 3, new PixelRect(1, 0, 2, 2), 90));
        Assert.Equal(new uint[] { 2, 3, 12, 13 }, Turn(Frame3x2, 3, new PixelRect(1, 0, 2, 2), 0));
    }

    [Fact]
    public void PaddedDestinationRows_KeepTheirPadding()
    {
        PixelSize size = VideoFrameBitmap.OutputSize(new PixelRect(0, 0, 3, 2), 90);
        int rowBytes = (size.Width * 4) + 8;
        var destination = new byte[rowBytes * size.Height];
        Array.Fill(destination, (byte)0xEE);

        VideoFrameBitmap.CopyRotated(
            MemoryMarshal.AsBytes(Frame3x2.AsSpan()), 3, new PixelRect(0, 0, 3, 2), 90, destination, rowBytes);

        for (int y = 0; y < size.Height; y++)
        {
            Assert.All(destination.AsSpan((y * rowBytes) + (size.Width * 4), 8).ToArray(), b => Assert.Equal((byte)0xEE, b));
        }
        Assert.Equal(new uint[] { 11, 1, 12, 2, 13, 3 }, Turn(Frame3x2, 3, new PixelRect(0, 0, 3, 2), 90, rowPadding: 8));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Turn_MatchesTheSkiaRotationTheOldPreviewUsed(int rotation)
    {
        // An odd-sized frame (so a centre falls between pixels) of opaque, all-distinct pixels, cropped off-centre.
        const int width = 7;
        const int height = 5;
        var frame = new uint[width * height];
        for (int i = 0; i < frame.Length; i++)
        {
            frame[i] = 0xFF000000u | (uint)(i * 0x010203);
        }

        var region = new PixelRect(1, 1, 5, 3);
        uint[] expected = SkiaTurn(frame, width, height, region, rotation);

        Assert.Equal(expected, Turn(frame, width, region, rotation));
    }

    // FloatingBitmapConversionHelper.TransformBitmap's canvas steps, on the region cut out of the frame first.
    private static uint[] SkiaTurn(uint[] frame, int width, int height, PixelRect region, int rotation)
    {
        using var source = new SKBitmap(new SKImageInfo(region.Width, region.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        Span<uint> sourcePixels = MemoryMarshal.Cast<byte, uint>(source.GetPixelSpan());
        for (int y = 0; y < region.Height; y++)
        {
            frame.AsSpan(((region.Y + y) * width) + region.X, region.Width).CopyTo(sourcePixels.Slice(y * region.Width));
        }

        bool swap = rotation is 90 or 270;
        int dstW = swap ? region.Height : region.Width;
        int dstH = swap ? region.Width : region.Height;
        using var turned = new SKBitmap(new SKImageInfo(dstW, dstH, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(turned))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(dstW / 2f, dstH / 2f);
            canvas.RotateDegrees(rotation);
            canvas.Translate(-region.Width / 2f, -region.Height / 2f);
            canvas.DrawBitmap(source, 0, 0);
        }

        return MemoryMarshal.Cast<byte, uint>(turned.GetPixelSpan()).ToArray();
    }
}
