using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using SkiaSharp;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// Real-encode checks for the exporter's composite hooks and the crop+rotate BGRA path (the dims fix):
/// the post-transform hook must draw on the CROPPED+ROTATED frame exactly where the editor preview shows
/// it. GATED like <see cref="CompressIntegrationTests"/> — a no-op pass unless COMPRESS_IT_SOURCE /
/// COMPRESS_IT_OUTDIR are set (Windows + bundled FFmpeg libs required). Drive via scripts/test-compress.ps1.
/// </summary>
public class LibavClipExporterCompositeTests
{
    private static string? Source => Environment.GetEnvironmentVariable("COMPRESS_IT_SOURCE");
    private static string? OutDir => Environment.GetEnvironmentVariable("COMPRESS_IT_OUTDIR");

    private static bool Enabled =>
        !string.IsNullOrWhiteSpace(Source) && File.Exists(Source) && !string.IsNullOrWhiteSpace(OutDir);

    [Fact]
    public async Task CropPlusRotate_WithPostTransformComposite_BurnsMarkerAtDrawnPosition()
    {
        if (!Enabled)
        {
            return; // gate: does nothing unless the runner provides a source + output dir
        }

        string source = Source!;
        string outDir = OutDir!;
        Directory.CreateDirectory(outDir);

        var player = new LibavVideoFramePlayer();
        double duration = await player.ProbeDurationSecondsAsync(source) ?? 0;
        var srcSize = await player.ProbeVideoSizeAsync(source);
        Assert.NotNull(srcSize);
        Assert.True(duration > 0.5, "source too short");

        // Crop the top-left quarter (even dims), rotate 90° clockwise.
        int cropW = (srcSize!.Value.Width / 2) & ~1;
        int cropH = (srcSize.Value.Height / 2) & ~1;
        var crop = new VideoEditCrop(0, 0, cropW, cropH);
        // After 90° rotation the frame transposes: rotW×rotH = cropH×cropW.
        int rotW = cropH, rotH = cropW;

        var ranges = new[] { new LibavClipExporter.SourceRange(0, Math.Min(1.0, duration)) };

        // The post-transform composite paints a solid red square in the TOP-LEFT of the rotated frame —
        // surface space, exactly like the compress editor burns annotations drawn on its preview.
        int marker = Math.Min(40, Math.Min(rotW, rotH) / 4);
        Action<SKBitmap, double> post = (sk, _) =>
        {
            Assert.Equal(rotW, sk.Width);   // hook must receive the CROPPED+ROTATED dims
            Assert.Equal(rotH, sk.Height);
            using var canvas = new SKCanvas(sk);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(new SKRect(0, 0, marker, marker), paint);
        };

        string outPath = Path.Combine(outDir, "composite_crop_rot90.mp4");
        bool ok = await Task.Run(() => LibavClipExporter.TryExport(
            source, ranges, outPath, VideoQuality.Medium, crop: crop,
            options: new LibavExportOptions { RotationDegrees = 90, DropAudio = true },
            frameCompositeAfterTransform: post));
        Assert.True(ok && File.Exists(outPath), "composite crop+rotate export failed");

        // Output dims = rotated crop dims (no downscale requested).
        var outSize = await player.ProbeVideoSizeAsync(outPath);
        Assert.NotNull(outSize);
        Assert.Equal(rotW, outSize!.Value.Width);
        Assert.Equal(rotH, outSize.Value.Height);

        // Decode a frame and assert the marker landed top-left (red) while the far corner is not red.
        byte[]? frame = await LibavVideoFramePlayer.DecodeFrameAtAsync(outPath, 0.3, rotW, rotH, default);
        Assert.NotNull(frame);
        (byte b1, byte g1, byte r1) = Pixel(frame!, rotW, marker / 2, marker / 2);
        Assert.True(r1 > 150 && g1 < 110 && b1 < 110, $"marker not red at top-left: R={r1} G={g1} B={b1}");
        (byte b2, byte g2, byte r2) = Pixel(frame!, rotW, rotW - marker, rotH - marker);
        Assert.False(r2 > 200 && g2 < 60 && b2 < 60, "far corner unexpectedly solid red");
    }

    [Fact]
    public async Task CropPlusRotate_WithoutComposite_ProducesRotatedCropDims()
    {
        if (!Enabled)
        {
            return;
        }

        string source = Source!;
        string outDir = OutDir!;
        Directory.CreateDirectory(outDir);

        var player = new LibavVideoFramePlayer();
        double duration = await player.ProbeDurationSecondsAsync(source) ?? 0;
        var srcSize = await player.ProbeVideoSizeAsync(source);
        Assert.NotNull(srcSize);

        int cropW = (srcSize!.Value.Width / 2) & ~1;
        int cropH = (srcSize.Value.Height / 2) & ~1;
        var crop = new VideoEditCrop(0, 0, cropW, cropH);
        var ranges = new[] { new LibavClipExporter.SourceRange(0, Math.Min(1.0, duration)) };

        // Regression for the crop+rotate dims fix: this combination previously ran the BGRA path with
        // full-decode-dims contexts against a crop-sized frame.
        string outPath = Path.Combine(outDir, "crop_rot90_nocomposite.mp4");
        bool ok = await Task.Run(() => LibavClipExporter.TryExport(
            source, ranges, outPath, VideoQuality.Medium, crop: crop,
            options: new LibavExportOptions { RotationDegrees = 90, DropAudio = true }));
        Assert.True(ok && File.Exists(outPath), "crop+rotate export failed");

        var outSize = await player.ProbeVideoSizeAsync(outPath);
        Assert.NotNull(outSize);
        Assert.Equal(cropH, outSize!.Value.Width);  // 90° transposes
        Assert.Equal(cropW, outSize.Value.Height);
    }

    private static (byte B, byte G, byte R) Pixel(byte[] bgra, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2]);
    }
}
