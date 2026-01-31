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

using System.Collections.Generic;
using GimmeCapture.Models;
using Avalonia.Media;
using System.Linq;

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

    public async Task<SKBitmap> CaptureScreenWithAnnotationsAsync(Rect region, IEnumerable<Annotation> annotations)
    {
        var bitmap = await CaptureScreenAsync(region);
        if (annotations == null || !annotations.Any()) return bitmap;

        using (var canvas = new SKCanvas(bitmap))
        {
            foreach (var ann in annotations)
            {
                using var paint = new SKPaint
                {
                    Color = new SKColor(ann.Color.R, ann.Color.G, ann.Color.B, ann.Color.A),
                    IsAntialias = true,
                    StrokeWidth = (float)ann.Thickness,
                    Style = SKPaintStyle.Stroke
                };

                var p1 = new SKPoint((float)ann.StartPoint.X, (float)ann.StartPoint.Y);
                var p2 = new SKPoint((float)ann.EndPoint.X, (float)ann.EndPoint.Y);

                switch (ann.Type)
                {
                    case AnnotationType.Rectangle:
                        canvas.DrawRect(SKRect.Create(
                            (float)Math.Min(p1.X, p2.X), 
                            (float)Math.Min(p1.Y, p2.Y), 
                            Math.Abs(p1.X - p2.X), 
                            Math.Abs(p1.Y - p2.Y)), paint);
                        break;
                    case AnnotationType.Ellipse:
                        canvas.DrawOval(SKRect.Create(
                            (float)Math.Min(p1.X, p2.X), 
                            (float)Math.Min(p1.Y, p2.Y), 
                            Math.Abs(p1.X - p2.X), 
                            Math.Abs(p1.Y - p2.Y)), paint);
                        break;
                    case AnnotationType.Line:
                        canvas.DrawLine(p1, p2, paint);
                        break;
                    case AnnotationType.Arrow:
                        DrawArrow(canvas, p1, p2, paint);
                        break;
                    case AnnotationType.Text:
                        paint.Style = SKPaintStyle.Fill;
                        paint.TextSize = (float)ann.FontSize;
                        
                        // Create Typeface
                        var weight = ann.IsBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
                        var slant = ann.IsItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
                        var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);
                        
                        // Use provided font family or fallback to system default
                        using (var typeface = SKTypeface.FromFamilyName(ann.FontFamily, style)) 
                        {
                            paint.Typeface = typeface ?? SKTypeface.Default;
                            canvas.DrawText(ann.Text ?? string.Empty, p1, paint);
                            paint.Typeface = null; // Reset
                        }
                        break;
                }
            }
        }

        return bitmap;
    }

    private void DrawArrow(SKCanvas canvas, SKPoint p1, SKPoint p2, SKPaint paint)
    {
        canvas.DrawLine(p1, p2, paint);
        
        var angle = (float)Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
        var arrowSize = 15.0f + paint.StrokeWidth;
        var arrowAngle = (float)Math.PI / 6;

        var ap1 = new SKPoint(
            p2.X - arrowSize * (float)Math.Cos(angle - arrowAngle),
            p2.Y - arrowSize * (float)Math.Sin(angle - arrowAngle));
        
        var ap2 = new SKPoint(
            p2.X - arrowSize * (float)Math.Cos(angle + arrowAngle),
            p2.Y - arrowSize * (float)Math.Sin(angle + arrowAngle));

        paint.Style = SKPaintStyle.Fill;
        using var path = new SKPath();
        path.MoveTo(p2);
        path.LineTo(ap1);
        path.LineTo(ap2);
        path.Close();
        canvas.DrawPath(path, paint);
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
