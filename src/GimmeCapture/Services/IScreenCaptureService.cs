using Avalonia;
using SkiaSharp;
using System.Collections.Generic;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Services;

public interface IScreenCaptureService
{
    Task<SKBitmap> CaptureScreenAsync(Rect region, bool includeCursor = false);
    Task<SKBitmap> CaptureScreenWithAnnotationsAsync(Rect region, IEnumerable<Annotation> annotations, bool includeCursor = false);
    Task CopyToClipboardAsync(SKBitmap bitmap);
    Task CopyFileToClipboardAsync(string filePath);
    Task SaveToFileAsync(SKBitmap bitmap, string path);
}
