using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using SkiaSharp;

namespace GimmeCapture.Services.Platforms.Linux;

/// <summary>
/// Placeholder <see cref="IScreenCaptureService"/> for non-Windows (Linux) runs. The real backend
/// (X11 / PipeWire, or libav x11grab for a single frame) is future work — see
/// docs/LINUX_PORT_FEASIBILITY.md, Phase 1. Until then this returns blank bitmaps and no-ops the
/// clipboard so the app can start and exercise the file-based paths on Linux without crashing.
/// </summary>
public sealed class LinuxScreenCaptureService : IScreenCaptureService
{
    public Task<SKBitmap> CaptureScreenAsync(Rect region, PixelPoint screenOffset, double visualScaling, bool includeCursor = false)
    {
        AppLog.Information("LinuxScreenCapture.CaptureScreen.NotImplemented");
        return Task.FromResult(CreateBlank(region, visualScaling));
    }

    public Task<SKBitmap> CaptureScreenWithAnnotationsAsync(Rect region, PixelPoint screenOffset, double visualScaling,
        IEnumerable<Annotation> annotations,
        IEnumerable<UserSelectionRect>? translationSelections = null,
        IEnumerable<TranslatedBlock>? translationBlocks = null,
        bool includeCursor = false)
    {
        AppLog.Information("LinuxScreenCapture.CaptureScreenWithAnnotations.NotImplemented");
        return Task.FromResult(CreateBlank(region, visualScaling));
    }

    public Task CopyToClipboardAsync(SKBitmap bitmap)
    {
        AppLog.Information("LinuxScreenCapture.CopyImage.NotImplemented");
        return Task.CompletedTask;
    }

    public Task CopyToClipboardAsync(string text)
    {
        AppLog.Information("LinuxScreenCapture.CopyText.NotImplemented");
        return Task.CompletedTask;
    }

    public Task CopyFileToClipboardAsync(string filePath)
    {
        AppLog.Information("LinuxScreenCapture.CopyFile.NotImplemented");
        return Task.CompletedTask;
    }

    public Task SaveToFileAsync(SKBitmap bitmap, string path)
    {
        // Saving a bitmap to disk is OS-agnostic (SkiaSharp), so honour it even on the stub backend.
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = System.IO.File.OpenWrite(path);
        data.SaveTo(stream);
        return Task.CompletedTask;
    }

    public Task<WriteableBitmap?> CaptureRegionBitmapAsync(Rect region, PixelPoint screenOffset, double visualScaling)
    {
        AppLog.Information("LinuxScreenCapture.CaptureRegionBitmap.NotImplemented");
        return Task.FromResult<WriteableBitmap?>(null);
    }

    private static SKBitmap CreateBlank(Rect region, double visualScaling)
    {
        int width = (int)(region.Width * visualScaling);
        int height = (int)(region.Height * visualScaling);
        return new SKBitmap(width > 0 ? width : 1, height > 0 ? height : 1);
    }
}
