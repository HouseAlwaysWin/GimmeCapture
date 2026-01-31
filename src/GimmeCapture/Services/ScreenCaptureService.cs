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
             var topLevel = TopLevel.GetTopLevel(Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null);
             if (topLevel?.Clipboard is { } clipboard)
             {
                 // Convert SKBitmap to Avalonia Bitmap
                 using var image = SKImage.FromBitmap(bitmap);
                 using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                 using var stream = data.AsStream();
                 
                 // Avalonia 11+ way requires using a platform specific way or creating a DataObject with 'PNG' format
                 // Since SetBitmapAsync might not be directly available on IClipboard in strict sense depending on version
                 // We construct a DataObject with file or raw stream.
                 
                 // But actually, let's try to pass the stream as a standard bitmap format.
                 // NOTE: As of now, Avalonia's Clipboard API is basic.
                 // Best practice for cross-platform image clipboard often involves tmp file or P/Invoke.
                 // However, let's try the modern DataObject approach if available.
                 
                 var dataObject = new DataObject();
                 // "PNG" format is standard
                 // We need to keep stream open? No, DataObject usually consumes it.
                 // Actually, we must create a byte array.
                 
                 using var memoryStream = new MemoryStream();
                 stream.CopyTo(memoryStream);
                 var bytes = memoryStream.ToArray();
                 
                 // Standard Clipboard formats usually expect specific naming or handling.
                 // Let's use a simpler hack for Windows: Save to temp, add to file list.
                 // Most apps (Discord, Teams, Slack) handle file copy as image upload.
                 // Paint handles it? No.
                 
                 // REAL SOLUTION: Use System.Windows.Forms on Windows (since we have System.Drawing.Common)
                 if (OperatingSystem.IsWindows())
                 {
                     try
                     {
                         // We need a STA thread for Windows Forms Clipboard.
                         // Avalonia UI thread is STA.
                         // But we need to convert SKBitmap (via stream) -> System.Drawing.Bitmap
                         using var ms = new MemoryStream(bytes);
                         using var sysBitmap = new System.Drawing.Bitmap(ms);
                         
                         // This call might require <UseWindowsForms>true</UseWindowsForms> in csproj
                         // But System.Drawing.Common doesn't give Clipboard.
                         // We might need to P/Invoke or rely on Avalonia.
                     }
                     catch {}
                 }
                 
                 // Let's stick to Avalonia DataObject.
                 // Some sources say: dataObject.Set(DataFormats.Bitmap, avaloniaBitmap);
                 // We need to create an Avalonia Bitmap from stream.
                 
                 memoryStream.Position = 0;
                 var avaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(memoryStream);
                 
                 // dataObject.Set(DataFormats.Bitmap, avaloniaBitmap); // If this works
                 // Wait, DataFormats.Bitmap is string "Bitmap".
                 
                 // DataFormats.Bitmap might not exist in this version of Avalonia.Input?
                 // Standard formats: "Text", "FileDrop".
                 // For Bitmap, it's often platform specific string.
                 // However, Avalonia should handle the key "Bitmap" internally if we pass a Bitmap object.
                 
                 // Fix: Use DataFormats.Text for text, but for custom/bitmap, we use raw string "Bitmap" or look up correct field.
                 // Actually, let's try calling DataObject.Set("Bitmap", avaloniaBitmap);
                 
                 dataObject.Set("Bitmap", avaloniaBitmap);
                 
                 // Reverting to SetDataObjectAsync as SetDataAsync with 2 args doesn't match this version's API
                 #pragma warning disable CS0618 // Type or member is obsolete
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
