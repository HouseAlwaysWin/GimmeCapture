using System;
using System.Diagnostics;
using GimmeCapture.ViewModels.Main;
using SkiaSharp;
using Xunit.Abstractions;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// The copy of a capture the translation overlay reads text from, no longer than a set size. The OCR search made it
/// from a grab of the whole screen on the UI thread; it is now made on the pool with the detection.
/// </summary>
public sealed class SnipWindowViewModelTranslationBitmapTests
{
    private readonly ITestOutputHelper _output;

    public SnipWindowViewModelTranslationBitmapTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(1920, 1080, 1600, 900)]
    [InlineData(3968, 1160, 1600, 468)] // two screens side by side
    [InlineData(1080, 1920, 900, 1600)] // portrait: the height is the longer side
    public void ALargerCapture_IsBroughtDown_ToTheLongestSideAllowed(
        int width, int height, int expectedWidth, int expectedHeight)
    {
        using var capture = new SKBitmap(width, height);

        using SKBitmap? copy = SnipWindowViewModel.CreateOptimizedTranslationBitmap(capture, 1.25, 1600, out double scale);

        Assert.NotNull(copy);
        Assert.Equal((expectedWidth, expectedHeight), (copy.Width, copy.Height));
        Assert.Equal(1.25 * 1600 / Math.Max(width, height), scale, 6);
    }

    [Fact]
    public void ACaptureSmallEnough_IsReadAsItIs()
    {
        using var capture = new SKBitmap(1200, 800);

        Assert.Null(SnipWindowViewModel.CreateOptimizedTranslationBitmap(capture, 1.25, 1600, out double scale));
        Assert.Equal(1.25, scale);
    }

    /// <summary>
    /// How long the copy of a whole screen takes, which the OCR search spent on the UI thread. Opt-in:
    /// OCR_SEARCH_RESIZE_PROBE=1.
    /// </summary>
    [OptInFact("OCR_SEARCH_RESIZE_PROBE=1")]
    public void AWholeScreensCopy_Timed()
    {
        foreach ((int width, int height) in new[] { (1920, 1080), (2560, 1440), (3968, 1160), (3840, 2160) })
        {
            using SKBitmap screen = Noise(width, height);
            SnipWindowViewModel.CreateOptimizedTranslationBitmap(screen, 1, 1600, out _)?.Dispose();
            var times = new double[5];
            for (int i = 0; i < times.Length; i++)
            {
                var watch = Stopwatch.StartNew();
                SnipWindowViewModel.CreateOptimizedTranslationBitmap(screen, 1, 1600, out _)?.Dispose();
                times[i] = watch.Elapsed.TotalMilliseconds;
            }

            Array.Sort(times);
            _output.WriteLine($"{width}x{height}: {times[0]:F1}-{times[^1]:F1} ms, median {times[2]:F1}");
        }
    }

    // Detail everywhere, as a screen of text has, so the resampler cannot skip flat areas.
    private static SKBitmap Noise(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        var random = new Random(1);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        return bitmap;
    }
}
