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
    [DllImport("user32.dll")]
    static extern bool GetCursorInfo(out CURSORINFO pci);

    [DllImport("user32.dll")]
    static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO
    {
        public Int32 cbSize;
        public Int32 flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public Int32 x;
        public Int32 y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public bool fIcon;
        public Int32 xHotspot;
        public Int32 yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    public async Task<SKBitmap> CaptureScreenAsync(Rect region, PixelPoint screenOffset, double visualScaling, bool includeCursor = false)
    {
        return await Task.Run(() =>
        {
            if (OperatingSystem.IsWindows())
            {
                // Calculate physical pixels
                int x = (int)((region.X + screenOffset.X) * visualScaling);
                int y = (int)((region.Y + screenOffset.Y) * visualScaling);
                int width = (int)(region.Width * visualScaling);
                int height = (int)(region.Height * visualScaling);

                if (width <= 0 || height <= 0) return new SKBitmap(1, 1);

                using var bitmap = new Bitmap(width, height);
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
                    
                    if (includeCursor)
                    {
                        try 
                        {
                            CURSORINFO pci;
                            pci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                            if (GetCursorInfo(out pci) && pci.flags == 0x00000001) // CURSOR_SHOWING
                            {
                                var hIcon = CopyIcon(pci.hCursor);
                                if (hIcon != IntPtr.Zero)
                                {
                                    try
                                    {
                                        ICONINFO ii;
                                        if (GetIconInfo(hIcon, out ii))
                                        {
                                            int cursorX = pci.ptScreenPos.x - x - ii.xHotspot;
                                            int cursorY = pci.ptScreenPos.y - y - ii.yHotspot;
                                            
                                            using var icon = Icon.FromHandle(hIcon);
                                            g.DrawIcon(icon, cursorX, cursorY);
                                            
                                            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                                            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                                        }
                                    }
                                    finally
                                    {
                                        DestroyIcon(hIcon);
                                    }
                                }
                            }
                        }
                        catch { /* Ignore cursor errors */ }
                    }
                }
                
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

    public async Task<SKBitmap> CaptureScreenWithAnnotationsAsync(Rect region, PixelPoint screenOffset, double visualScaling, IEnumerable<Annotation> annotations, bool includeCursor = false)
    {
        var bitmap = await CaptureScreenAsync(region, screenOffset, visualScaling, includeCursor);
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
                        
                        // Create Typeface with Fallback Logic
                        var weight = ann.IsBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
                        var slant = ann.IsItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
                        var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);
                        
                        SKTypeface typeface = SKTypeface.FromFamilyName(ann.FontFamily, style);
                        
                        // Fallback Check: If text contains non-ASCII and primary font might not support it
                        if (!string.IsNullOrEmpty(ann.Text))
                        {
                            // Optimized: Check if any char is missing glyph in current typeface
                            bool missingGlyph = false;
                            
                            // 1. Check if typeface is valid, if null defaulted to something, potentially missing chars
                            if (typeface == null) typeface = SKTypeface.Default;
                            
                            // 2. Check for missing glyphs
                            // Convert string to code points (simplification: simple char iteration usually enough for BMP)
                            var ids = new ushort[ann.Text.Length];
                            using(var font = new SKFont(typeface))
                            {
                                font.GetGlyphs(ann.Text, ids);
                                // If any glyph ID is 0, it means missing
                                if (ids.Any(id => id == 0))
                                {
                                    missingGlyph = true;
                                }
                            }
                            
                            if (missingGlyph)
                            {
                                // Try to find a fallback that supports the text
                                // We pick the first non-supported char or just a common fallback
                                // On Windows, "Microsoft YaHei" is a good bet for CJK
                                var fallback = SKFontManager.Default.MatchCharacter(ann.Text.FirstOrDefault(c => c > 127));
                                if (fallback != null)
                                {
                                    typeface.Dispose();
                                    typeface = fallback;
                                }
                            }
                        }

                        using (typeface) // Ensure we dispose it (MatchCharacter returns a new Ref)
                        {
                            // Ensure Paint uses this typeface
                            paint.Typeface = typeface;
                            
                            // Draw
                            canvas.DrawText(ann.Text ?? string.Empty, p1, paint);
                            paint.Typeface = null; // Detach before disposal
                        }
                        break;
                    case AnnotationType.Pen:
                        if (ann.Points.Any())
                        {
                            using var path = new SKPath();
                            var first = ann.Points.First();
                            path.MoveTo((float)first.X, (float)first.Y);
                            foreach (var p in ann.Points.Skip(1))
                            {
                                path.LineTo((float)p.X, (float)p.Y);
                            }
                            canvas.DrawPath(path, paint);
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

    public async Task CopyFileToClipboardAsync(string filePath)
    {
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var topLevel = TopLevel.GetTopLevel(Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop ? desktop.MainWindow : null);
            
            if (topLevel?.Clipboard is { } clipboard)
            {
                var dataObject = new DataObject();
                dataObject.Set(DataFormats.Files, new[] { filePath });
                await clipboard.SetDataObjectAsync(dataObject);
                System.Diagnostics.Debug.WriteLine($"Avalonia Clipboard: Copied file {filePath}");
            }
            else
            {
                 System.Diagnostics.Debug.WriteLine("Avalonia Clipboard: Clipboard not available");
            }
        });
    }
}
