using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Microsoft.ML.OnnxRuntime.Tensors;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

public class BackgroundRemovalServiceTests
{
    // The AI folder of an install with the AI Core module (u2net.onnx), for the opt-in measurement.
    private const string ModelsVariable = "AI_MODELS_DIR";
    private const long Mb = 1024 * 1024;

    private readonly ITestOutputHelper _output;

    public BackgroundRemovalServiceTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// How much of the process the real U2Net model keeps: loaded, after three removals of a picture the size of two
    /// screens, and once unloaded. On DirectML that was 1.09 GB, then about 480 MB while the device was kept for the next
    /// removal and 255 MB for good; on the CPU with ONNX Runtime's arena, 1.24 GB after two runs. Now about 190 MB and
    /// 20. Opt-in, naming the AI folder of an install with the AI Core module (<c>AI_MODELS_DIR</c>, usually
    /// %LOCALAPPDATA%\GimmeCapture\AI), and best run on its own: it reads the private bytes of the whole test process.
    /// It also reports how long the removals take.
    /// </summary>
    [OptInFact(ModelsVariable)]
    public async Task TheRealModel_KeepsLittleOfTheProcess_LoadedOrNot()
    {
        string folder = Environment.GetEnvironmentVariable(ModelsVariable)!;
        string baseDir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettingsService(baseDir);
            settings.Settings.AIResourcesDirectory = folder;
            var pathService = new AIPathService(settings);
            var aiResources = new AIResourceService(settings, pathService, new AIModelDownloader());
            Assert.True(aiResources.IsAICoreReady(), $"No AI Core module in {folder}.");
            using var scene = TestBitmaps.Scene(3968, 1160);
            long baseline = PrivateBytes();

            var service = new BackgroundRemovalService(aiResources, pathService);
            var times = new List<long>();
            for (int i = 0; i < 3; i++)
            {
                var stopwatch = Stopwatch.StartNew();
                using var removed = await service.RemoveBackgroundAsync(scene);
                times.Add(stopwatch.ElapsedMilliseconds);
            }

            long loaded = PrivateBytes() - baseline;
            service.Dispose();
            long unloaded = PrivateBytes() - baseline;
            _output.WriteLine(
                $"Removals took {string.Join(", ", times)} ms, the first with the load. Over the baseline: " +
                $"{loaded / Mb} MB loaded, {unloaded / Mb} MB unloaded.");

            Assert.InRange(loaded, long.MinValue, 300 * Mb);
            Assert.InRange(unloaded, long.MinValue, 40 * Mb);
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { /* best effort temp cleanup */ }
        }
    }

    [Fact]
    public void IsSolidBackground_DetectsUniformOpaqueEdges()
    {
        using var bitmap = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(new SKColor(240, 240, 240, 255));

        bool result = BackgroundRemovalService.IsSolidBackground(bitmap, out SKColor backgroundColor);

        Assert.True(result);
        Assert.Equal((byte)240, backgroundColor.Red);
        Assert.Equal((byte)240, backgroundColor.Green);
        Assert.Equal((byte)240, backgroundColor.Blue);
    }

    [Fact]
    public void TryGetCornerBackgroundColor_DetectsDominantCornerColor()
    {
        using var bitmap = new SKBitmap(40, 40, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.White);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill };
        canvas.DrawRect(new SKRect(10, 10, 30, 30), paint);

        bool result = BackgroundRemovalService.TryGetCornerBackgroundColor(bitmap, out SKColor backgroundColor);

        Assert.True(result);
        Assert.Equal(SKColors.White.Red, backgroundColor.Red);
        Assert.Equal(SKColors.White.Green, backgroundColor.Green);
        Assert.Equal(SKColors.White.Blue, backgroundColor.Blue);
    }

    [Fact]
    public void RemoveSolidBackground_MakesMatchingPixelsTransparent()
    {
        using var bitmap = new SKBitmap(2, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.White);
        bitmap.SetPixel(1, 0, SKColors.Black);

        using var result = BackgroundRemovalService.RemoveSolidBackground(bitmap, SKColors.White);

        Assert.Equal((byte)0, result.GetPixel(0, 0).Alpha);
        Assert.Equal((byte)255, result.GetPixel(1, 0).Alpha);
    }

    [Fact]
    public void ProcessMask_ResizesToSingleChannelMask()
    {
        var tensor = CreateLeftOpaqueMaskTensor();

        using var mask = BackgroundRemovalService.ProcessMask(tensor, 435, 434, out _, out _);

        Assert.Equal(435, mask.Width);
        Assert.Equal(434, mask.Height);
        Assert.Equal(SKColorType.Gray8, mask.ColorType);
    }

    [Fact]
    public void ApplyMask_UsesResizedMaskAcrossFullWidth()
    {
        using var bitmap = new SKBitmap(435, 4, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        bitmap.Erase(SKColors.Red);
        var tensor = CreateLeftOpaqueMaskTensor();
        using var mask = BackgroundRemovalService.ProcessMask(tensor, bitmap.Width, bitmap.Height, out _, out _);

        using var result = BackgroundRemovalService.ApplyMask(bitmap, mask, 0, 1);

        Assert.True(result.GetPixel(20, 2).Alpha > 240);
        Assert.True(result.GetPixel(420, 2).Alpha < 15);
    }

    // Every source alpha (row) against every mask value (column): the lower of the two, 0 below 10, the source's
    // own alpha above 240, and the colour as it was.
    [Fact]
    public void ApplyMask_TakesTheLowerAlpha_AndCutsAtTheThresholds()
    {
        using var source = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var mask = new SKBitmap(256, 256, SKColorType.Gray8, SKAlphaType.Opaque);
        var pixels = new uint[256 * 256];
        var maskValues = new byte[256 * 256];
        for (int alpha = 0; alpha < 256; alpha++)
        {
            for (int value = 0; value < 256; value++)
            {
                pixels[(alpha * 256) + value] = ((uint)alpha << 24) | ((uint)value << 16) | ((uint)(255 - alpha) << 8) | 77u;
                maskValues[(alpha * 256) + value] = (byte)value;
            }
        }

        WritePixels(source, pixels);
        WritePixels(mask, maskValues);

        using var result = BackgroundRemovalService.ApplyMask(source, mask, 0, 1);

        uint[] actual = ReadPixels(result);
        for (int i = 0; i < pixels.Length; i++)
        {
            int sourceAlpha = (int)(pixels[i] >> 24);
            int expectedAlpha = Math.Min(sourceAlpha, maskValues[i]);
            if (expectedAlpha < 10) expectedAlpha = 0;
            if (expectedAlpha > 240) expectedAlpha = sourceAlpha;
            Assert.Equal(((uint)expectedAlpha << 24) | (pixels[i] & 0x00FFFFFFu), actual[i]);
        }
    }

    // The resize keeps the two interpolated source rows a target row blends and runs in bands of rows on several
    // cores; neither may change a byte of the plain bilinear resize, up or down, at any band boundary. The last
    // case holds the logits in a column-major tensor, which is read through the indexer instead of the buffer.
    [Theory]
    [InlineData(435, 434, false)]
    [InlineData(100, 50, false)]
    [InlineData(321, 319, false)]
    [InlineData(1000, 7, false)]
    [InlineData(7, 1000, false)]
    [InlineData(1, 1, false)]
    [InlineData(640, 2400, false)]
    [InlineData(435, 434, true)]
    public void ProcessMask_GivesThePlainBilinearResize(int width, int height, bool columnMajor)
    {
        var tensor = CreateSmoothMaskTensor(columnMajor);
        using var mask320 = BackgroundRemovalService.ProcessMask(CreateSmoothMaskTensor(columnMajor: false), 320, 320, out _, out _);

        using var mask = BackgroundRemovalService.ProcessMask(tensor, width, height, out _, out _);

        byte[] expected = ResizeBilinear(ReadPixels<byte>(mask320), 320, 320, width, height);
        Assert.Equal(expected, ReadPixels<byte>(mask));
    }

    [Fact]
    public void RemoveSolidBackground_ClearsWithinTheToleranceOfEachChannel_AndKeepsTheRest()
    {
        var background = new SKColor(100, 150, 200);
        SKColor[] colors =
        [
            new(160, 150, 200, 255), new(40, 90, 140, 200), new(100, 150, 200, 9),
            new(161, 150, 200, 255), new(100, 89, 200, 255), new(100, 150, 139, 128),
        ];
        using var bitmap = new SKBitmap(colors.Length, 1, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        WritePixels(bitmap, colors.Select(color => (uint)color).ToArray());

        using var result = BackgroundRemovalService.RemoveSolidBackground(bitmap, background);

        uint[] actual = ReadPixels(result);
        for (int i = 0; i < colors.Length; i++)
        {
            uint expected = i < 3 ? (uint)colors[i] & 0x00FFFFFFu : (uint)colors[i];
            Assert.Equal(expected, actual[i]);
        }
    }

    // ComposeSelection copies what a canvas would draw instead of drawing it, through a table of Skia's own answers.
    // Holds it to the canvas wherever drawing changes a pixel: every alpha against every value in each channel,
    // in the source (premultiplied as a pin is, colour above alpha included, as a pin holds after a removal) and in
    // the processed region, around opaque runs that are copied as they are.
    [Theory]
    [InlineData(SKAlphaType.Premul)]
    [InlineData(SKAlphaType.Unpremul)]
    public void ComposeSelection_GivesTheBytesACanvasDraws(SKAlphaType sourceAlphaType)
    {
        using var source = CreateEveryAlphaAgainstEveryValue(sourceAlphaType, 600, 512);
        using var processed = CreateEveryAlphaAgainstEveryValue(SKAlphaType.Unpremul, 512, 256);
        var region = SKRectI.Create(60, 250, 512, 256);

        using var drawn = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(drawn))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(source, 0, 0);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(processed, region.Left, region.Top, paint);
        }

        using var composed = BackgroundRemovalService.ComposeSelection(source, processed, region);

        Assert.Equal(ReadPixels(drawn), ReadPixels(composed));
    }

    // Collects as the idle trim does, whose second collection hands the heap's free space back to Windows.
    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static DenseTensor<float> CreateLeftOpaqueMaskTensor()
    {
        var data = new float[320 * 320];
        for (int y = 0; y < 320; y++)
        {
            int rowOffset = y * 320;
            for (int x = 0; x < 320; x++)
            {
                data[rowOffset + x] = x < 160 ? 6f : -6f;
            }
        }

        return new DenseTensor<float>(data, new[] { 1, 1, 320, 320 });
    }

    /// <summary>Soft blobs, like a real mask's logits, so most target pixels fall between source values.</summary>
    private static DenseTensor<float> CreateSmoothMaskTensor(bool columnMajor)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 1, 320, 320 }, reverseStride: columnMajor);
        for (int y = 0; y < 320; y++)
        {
            for (int x = 0; x < 320; x++)
            {
                tensor[0, 0, y, x] = (float)(Math.Sin(x / 17.0) * Math.Cos(y / 23.0) * 8);
            }
        }

        return tensor;
    }

    /// <summary>
    /// For y under 256 and x under 512, even columns hold every alpha (x / 2) and odd ones are opaque, with the
    /// row's value in each channel: blue y, green 255 - y, red 7y mod 256. Everything else is opaque.
    /// </summary>
    private static SKBitmap CreateEveryAlphaAgainstEveryValue(SKAlphaType alphaType, int width, int height)
    {
        var pixels = new uint[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = y < 256 && x < 512
                    ? ((uint)(x % 2 == 0 ? x / 2 : 255) << 24) | ((uint)((y * 7) & 255) << 16) | ((uint)(255 - y) << 8) | (uint)y
                    : 0xFF000000u | ((uint)((x + y) & 255) << 16) | ((uint)(y & 255) << 8) | (uint)(x & 255);
            }
        }

        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, alphaType);
        WritePixels(bitmap, pixels);
        return bitmap;
    }

    /// <summary>The bilinear resize ProcessMask has always done, a pixel at a time.</summary>
    private static byte[] ResizeBilinear(byte[] source, int sourceWidth, int sourceHeight, int width, int height)
    {
        var resized = new byte[width * height];
        double scaleX = sourceWidth / (double)width;
        double scaleY = sourceHeight / (double)height;
        for (int y = 0; y < height; y++)
        {
            double sourceY = ((y + 0.5d) * scaleY) - 0.5d;
            int y0 = Math.Clamp((int)Math.Floor(sourceY), 0, sourceHeight - 1);
            int y1 = Math.Min(y0 + 1, sourceHeight - 1);
            double fy = Math.Clamp(sourceY - y0, 0d, 1d);
            for (int x = 0; x < width; x++)
            {
                double sourceX = ((x + 0.5d) * scaleX) - 0.5d;
                int x0 = Math.Clamp((int)Math.Floor(sourceX), 0, sourceWidth - 1);
                int x1 = Math.Min(x0 + 1, sourceWidth - 1);
                double fx = Math.Clamp(sourceX - x0, 0d, 1d);
                int row0 = y0 * sourceWidth;
                int row1 = y1 * sourceWidth;
                double top = source[row0 + x0] + ((source[row0 + x1] - source[row0 + x0]) * fx);
                double bottom = source[row1 + x0] + ((source[row1 + x1] - source[row1 + x0]) * fx);
                resized[(y * width) + x] = (byte)Math.Clamp((int)Math.Round(top + ((bottom - top) * fy)), 0, 255);
            }
        }

        return resized;
    }

    private static uint[] ReadPixels(SKBitmap bitmap) => ReadPixels<uint>(bitmap);

    /// <summary>The bitmap's pixels row after row, without the padding at the end of each row.</summary>
    private static T[] ReadPixels<T>(SKBitmap bitmap) where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = bitmap.GetPixelSpan();
        int width = bitmap.Width;
        int rowBytes = bitmap.RowBytes;
        var pixels = new T[width * bitmap.Height];
        for (int y = 0; y < bitmap.Height; y++)
        {
            MemoryMarshal.Cast<byte, T>(bytes[(y * rowBytes)..])[..width].CopyTo(pixels.AsSpan(y * width));
        }

        return pixels;
    }

    private static void WritePixels<T>(SKBitmap bitmap, T[] pixels) where T : unmanaged
    {
        Span<byte> bytes = bitmap.GetPixelSpan();
        int width = bitmap.Width;
        int rowBytes = bitmap.RowBytes;
        for (int y = 0; y < bitmap.Height; y++)
        {
            pixels.AsSpan(y * width, width).CopyTo(MemoryMarshal.Cast<byte, T>(bytes[(y * rowBytes)..]));
        }
    }
}
