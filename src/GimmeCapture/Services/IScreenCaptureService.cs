using Avalonia;
using SkiaSharp;
using System.Threading.Tasks;

namespace GimmeCapture.Services;

public interface IScreenCaptureService
{
    Task<SKBitmap> CaptureScreenAsync(Rect region);
    Task CopyToClipboardAsync(SKBitmap bitmap);
    Task SaveToFileAsync(SKBitmap bitmap, string path);
}
