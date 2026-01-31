using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using SkiaSharp;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace GimmeCapture.Services;

public class ScreenCaptureService : IScreenCaptureService
{
    public async Task<SKBitmap> CaptureScreenAsync(Rect region)
    {
        return await Task.Run(() =>
        {
            if (OperatingSystem.IsWindows())
            {
                // Note: This needs HighDPI awareness in App.manifest to be accurate
                int x = (int)region.X;
                int y = (int)region.Y;
                int width = (int)region.Width;
                int height = (int)region.Height;

                if (width <= 0 || height <= 0) return new SKBitmap(1, 1);

                using var bitmap = new Bitmap(width, height);
                using var g = Graphics.FromImage(bitmap);
                
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
                
                // Convert System.Drawing.Bitmap to SKBitmap
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                stream.Seek(0, SeekOrigin.Begin);
                
                return SKBitmap.Decode(stream);
            }
            
            // Fallback for other platforms (not implemented in Phase 1)
            return new SKBitmap(100, 100);
        });
    }

    public async Task CopyToClipboardAsync(SKBitmap bitmap)
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
             if (OperatingSystem.IsWindows())
             {
                 /* 
                  * Windows specific implementation using System.Windows.Forms.Clipboard
                  * for maximum compatibility with other Windows apps.
                  */
                 try
                 {
                     using var image = SKImage.FromBitmap(bitmap);
                     using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                     using var stream = data.AsStream();
                     using var ms = new MemoryStream();
                     stream.CopyTo(ms);
                     ms.Position = 0;
                     
                     // Create System.Drawing.Bitmap
                     using var winBitmap = new System.Drawing.Bitmap(ms);
                     
                     // Set to Clipboard
                     // Note: System.Windows.Forms.Clipboard.SetImage requires STA thread.
                     // Avalonia UI thread is usually STA on Windows.
                     System.Windows.Forms.Clipboard.SetImage(winBitmap);
                     return;
                 }
                 catch (Exception ex)
                 {
                     System.Diagnostics.Debug.WriteLine($"WinForms Clipboard failed: {ex.Message}");
                     // Fallback to Avalonia implementation below
                 }
             }

             // Fallback / Non-Windows implementation
             var topLevel = TopLevel.GetTopLevel(Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null);
             if (topLevel?.Clipboard is { } clipboard)
             {
                 using var image = SKImage.FromBitmap(bitmap);
                 // ... rest of Avalonia implementation ...
                 // Simplified for brevity in replacement
                 
                 var dataObject = new DataObject();
                 using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                 using var stream = data.AsStream();
                 using var ms = new MemoryStream();
                 stream.CopyTo(ms);
                 ms.Position = 0;
                 
                 var avaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(ms);
                 dataObject.Set("Bitmap", avaloniaBitmap);
                 
                 #pragma warning disable CS0618
                 await clipboard.SetDataObjectAsync(dataObject);
                 #pragma warning restore CS0618
             }
        });
    }

    public async Task SaveToFileAsync(SKBitmap bitmap, string path)
    {
        await Task.Run(() =>
        {
            using var fs = File.OpenWrite(path);
            bitmap.Encode(fs, SKEncodedImageFormat.Png, 100);
        });
    }
}
