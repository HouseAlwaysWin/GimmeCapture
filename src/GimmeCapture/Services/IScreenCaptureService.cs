using Avalonia;
using SkiaSharp;
using System.Collections.Generic;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Services;

public interface IScreenCaptureService
{
    Task<SKBitmap> CaptureScreenAsync(Rect region);
    Task<SKBitmap> CaptureScreenWithAnnotationsAsync(Rect region, IEnumerable<Annotation> annotations);
    Task CopyToClipboardAsync(SKBitmap bitmap);
    Task CopyFileToClipboardAsync(string filePath);
    Task SaveToFileAsync(SKBitmap bitmap, string path);
}
