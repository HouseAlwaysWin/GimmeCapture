using Avalonia;
using SkiaSharp;
using System.Collections.Generic;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Abstractions;

public interface IScreenCaptureService
{
    Task<SKBitmap> CaptureScreenAsync(global::Avalonia.Rect region, global::Avalonia.PixelPoint screenOffset, double visualScaling, bool includeCursor = false);
    Task<SKBitmap> CaptureScreenWithAnnotationsAsync(global::Avalonia.Rect region, global::Avalonia.PixelPoint screenOffset, double visualScaling, 
        IEnumerable<Annotation> annotations, 
        IEnumerable<UserSelectionRect>? translationSelections = null,
        IEnumerable<TranslatedBlock>? translationBlocks = null,
        bool includeCursor = false);
    /// <summary>Copy an image to the clipboard. Returns <c>true</c> if the write succeeded, <c>false</c> if it
    /// failed or timed out. A failed write leaves the PREVIOUS clipboard content in place, so callers must not
    /// report a successful copy on <c>false</c> — that is what makes the next paste yield the previous image.</summary>
    Task<bool> CopyToClipboardAsync(SKBitmap bitmap);

    /// <summary>Copy text to the clipboard. Returns <c>true</c> if the write succeeded, <c>false</c> if it
    /// failed or timed out — so callers don't falsely report a successful copy.</summary>
    Task<bool> CopyToClipboardAsync(string text);
    Task CopyFileToClipboardAsync(string filePath);
    Task SaveToFileAsync(SKBitmap bitmap, string path);
    
    /// <summary>
    /// Captures a region as a WriteableBitmap (optimized for display/drawing).
    /// </summary>
    Task<global::Avalonia.Media.Imaging.WriteableBitmap?> CaptureRegionBitmapAsync(global::Avalonia.Rect region, global::Avalonia.PixelPoint screenOffset, double visualScaling);
}
