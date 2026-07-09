using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    private static IScreenCaptureService CreateDesignScreenCaptureService() => new DesignScreenCaptureService();

    private static IWindowDetectionService CreateDesignWindowDetectionService() => new DesignWindowDetectionService();

    private sealed class DesignScreenCaptureService : IScreenCaptureService
    {
        public Task<SKBitmap> CaptureScreenAsync(Rect region, PixelPoint screenOffset, double visualScaling, bool includeCursor = false)
            => Task.FromResult(new SKBitmap(1, 1));

        public Task<SKBitmap> CaptureScreenWithAnnotationsAsync(
            Rect region,
            PixelPoint screenOffset,
            double visualScaling,
            IEnumerable<Annotation> annotations,
            IEnumerable<UserSelectionRect>? translationSelections = null,
            IEnumerable<TranslatedBlock>? translationBlocks = null,
            bool includeCursor = false)
            => Task.FromResult(new SKBitmap(1, 1));

        public Task CopyToClipboardAsync(SKBitmap bitmap) => Task.CompletedTask;

        public Task<bool> CopyToClipboardAsync(string text) => Task.FromResult(true);

        public Task CopyFileToClipboardAsync(string filePath) => Task.CompletedTask;

        public Task SaveToFileAsync(SKBitmap bitmap, string path) => Task.CompletedTask;

        public Task<WriteableBitmap?> CaptureRegionBitmapAsync(Rect region, PixelPoint screenOffset, double visualScaling)
        {
            var bitmap = new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            return Task.FromResult<WriteableBitmap?>(bitmap);
        }
    }

    private sealed class DesignWindowDetectionService : IWindowDetectionService
    {
        public IReadOnlyList<WindowCandidate> GetVisibleWindowCandidates(IntPtr? excludeHWnd = null) => [];

        public WindowCandidate? GetCandidateAtPoint(Point point, IReadOnlyList<WindowCandidate> candidates, WindowCandidate? previousCandidate = null) => null;

        public IReadOnlyList<RecordableWindow> GetRecordableWindows(IntPtr? excludeHWnd = null) => [];
    }
}
