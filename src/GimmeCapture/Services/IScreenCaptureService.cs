using Avalonia;
using SkiaSharp;
using System.Collections.Generic;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Services;

public interface IScreenCaptureService
{
    Task<SKBitmap> CaptureScreenAsync(Avalonia.Rect region, Avalonia.PixelPoint screenOffset, double visualScaling, bool includeCursor = false);
    Task<SKBitmap> CaptureScreenWithAnnotationsAsync(Avalonia.Rect region, Avalonia.PixelPoint screenOffset, double visualScaling, IEnumerable<Annotation> annotations, bool includeCursor = false);
    Task CopyToClipboardAsync(SKBitmap bitmap);
    Task CopyFileToClipboardAsync(string filePath);
    Task SaveToFileAsync(SKBitmap bitmap, string path);
}
