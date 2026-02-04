using Avalonia.Media.Imaging;
using System.Threading.Tasks;

namespace GimmeCapture.Services;

public interface IClipboardService
{
    Task CopyImageAsync(Bitmap bitmap);
    Task CopyTextAsync(string text);
    Task CopyFileAsync(string filePath);
}
