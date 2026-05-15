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
    Task CopyToClipboardAsync(SKBitmap bitmap);
    Task CopyToClipboardAsync(string text);
    Task CopyFileToClipboardAsync(string filePath);
    Task SaveToFileAsync(SKBitmap bitmap, string path);
    
    /// <summary>
    /// Captures a region as a WriteableBitmap (optimized for display/drawing).
    /// </summary>
    Task<global::Avalonia.Media.Imaging.WriteableBitmap?> CaptureRegionBitmapAsync(global::Avalonia.Rect region, global::Avalonia.PixelPoint screenOffset, double visualScaling);
}
