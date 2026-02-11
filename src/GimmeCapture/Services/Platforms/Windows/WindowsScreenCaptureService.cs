using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using SkiaSharp;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using Avalonia.Controls;


using System.Collections.Generic;
using GimmeCapture.Models;
using Avalonia.Media;
using System.Linq;

namespace GimmeCapture.Services.Platforms.Windows;

public class WindowsScreenCaptureService : IScreenCaptureService
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
                // Logical X * Scaling + Physical Offset (already scaled by OS if it's Position)
                int x = (int)(region.X * visualScaling) + screenOffset.X;
                int y = (int)(region.Y * visualScaling) + screenOffset.Y;
                int width = (int)(region.Width * visualScaling);
                int height = (int)(region.Height * visualScaling);

                if (width <= 0 || height <= 0) return new SKBitmap(1, 1);

                using var bitmap = new System.Drawing.Bitmap(width, height);
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
            
            return new SKBitmap(100, 100);
        });
    }

    public async Task<Avalonia.Media.Imaging.WriteableBitmap?> CaptureRegionBitmapAsync(Avalonia.Rect region, Avalonia.PixelPoint screenOffset, double visualScaling)
    {
        if (!OperatingSystem.IsWindows()) return null;

        return await Task.Run(() =>
        {
             try
            {
                // Calculate physical pixels for the selection area
                // Convert selection logical coordinates to physical and add window physical position
                int xPhysical = (int)(region.X * visualScaling) + screenOffset.X;
                int yPhysical = (int)(region.Y * visualScaling) + screenOffset.Y;
                int widthPhysical = (int)(region.Width * visualScaling);
                int heightPhysical = (int)(region.Height * visualScaling);

                if (widthPhysical <= 0 || heightPhysical <= 0) return null;

                // Use WriteableBitmap to avoid MemoryStream & PNG Encoding overhead
                var writeableBitmap = new Avalonia.Media.Imaging.WriteableBitmap(
                    new PixelSize(widthPhysical, heightPhysical), 
                    new Vector(96, 96), 
                    Avalonia.Platform.PixelFormat.Bgra8888, 
                    Avalonia.Platform.AlphaFormat.Premul);

                using (var lockedBitmap = writeableBitmap.Lock())
                {
                    // We still use GDI+ to capture the screen, but we copy bits directly to the WriteableBitmap
                    using var screenBmp = new System.Drawing.Bitmap(widthPhysical, heightPhysical, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = System.Drawing.Graphics.FromImage(screenBmp))
                    {
                        g.CopyFromScreen(
                            xPhysical, 
                            yPhysical, 
                            0, 0, 
                            new System.Drawing.Size(widthPhysical, heightPhysical));
                    }

                    var bmpData = screenBmp.LockBits(
                        new System.Drawing.Rectangle(0, 0, widthPhysical, heightPhysical),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    // Copy memory
                    for (int y = 0; y < heightPhysical; y++)
                    {
                       // Source Row
                       IntPtr srcRow = bmpData.Scan0 + (y * bmpData.Stride);
                       // Dest Row
                       IntPtr destRow = lockedBitmap.Address + (y * lockedBitmap.RowBytes);
                       
                       unsafe
                       {
                           Buffer.MemoryCopy(
                               (void*)srcRow, 
                               (void*)destRow, 
                               lockedBitmap.RowBytes, 
                               widthPhysical * 4);
                       }
                    }

                    screenBmp.UnlockBits(bmpData);
                }
                
                return writeableBitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to capture region: {ex.Message}");
                return null;
            }
        });
    }

    public async Task<SKBitmap> CaptureScreenWithAnnotationsAsync(Rect region, PixelPoint screenOffset, double visualScaling, IEnumerable<Annotation> annotations, bool includeCursor = false)
    {
        var bitmap = await CaptureScreenAsync(region, screenOffset, visualScaling, includeCursor);
        if (annotations == null || !annotations.Any()) return bitmap;

        // Use the visualScaling to adjust all logical coordinates to physical coordinates
        float scale = (float)visualScaling;

        using (var canvas = new SKCanvas(bitmap))
        {
            foreach (var ann in annotations)
            {
                using var paint = new SKPaint
                {
                    Color = new SKColor(ann.Color.R, ann.Color.G, ann.Color.B, ann.Color.A),
                    IsAntialias = true,
                    StrokeWidth = (float)(ann.Thickness * visualScaling),
                    Style = SKPaintStyle.Stroke
                };

                var p1 = new SKPoint((float)(ann.StartPoint.X * visualScaling), (float)(ann.StartPoint.Y * visualScaling));
                var p2 = new SKPoint((float)(ann.EndPoint.X * visualScaling), (float)(ann.EndPoint.Y * visualScaling));

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
                        DrawArrow(canvas, p1, p2, paint, scale);
                        break;
                    case AnnotationType.Text:
                        paint.Style = SKPaintStyle.Fill;
                        // paint.TextSize and Typeface are deprecated, moved to SKFont logic below
                        
                        // Create Typeface with Fallback Logic
                        var weight = ann.IsBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
                        var slant = ann.IsItalic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright;
                        var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, slant);
                        
                        SKTypeface typeface = SKTypeface.FromFamilyName(ann.FontFamily, style);
                        
                        // Fallback Check
                        if (!string.IsNullOrEmpty(ann.Text))
                        {
                            bool missingGlyph = false;
                            if (typeface == null) typeface = SKTypeface.Default;
                            
                            var ids = new ushort[ann.Text.Length];
                            using(var fontCheck = new SKFont(typeface))
                            {
                                fontCheck.GetGlyphs(ann.Text, ids);
                                if (ids.Any(id => id == 0)) missingGlyph = true;
                            }
                            
                            if (missingGlyph)
                            {
                                var fallback = SKFontManager.Default.MatchCharacter(ann.Text.FirstOrDefault(c => c > 127));
                                if (fallback != null)
                                {
                                    typeface.Dispose();
                                    typeface = fallback;
                                }
                            }
                        }

                        using (typeface)
                        {
                            // Create SKFont for drawing - Apply scale to FontSize
                            using var font = new SKFont(typeface, (float)(ann.FontSize * visualScaling));
                            
                            // Draw using new API
                            canvas.DrawText(ann.Text ?? string.Empty, p1, SKTextAlign.Left, font, paint);
                        }
                        break;
                    case AnnotationType.Pen:
                        if (ann.Points.Any())
                        {
                            using var path = new SKPath();
                            var first = ann.Points.First();
                            path.MoveTo((float)(first.X * visualScaling), (float)(first.Y * visualScaling));
                            foreach (var p in ann.Points.Skip(1))
                            {
                                path.LineTo((float)(p.X * visualScaling), (float)(p.Y * visualScaling));
                            }
                            canvas.DrawPath(path, paint);
                        }
                        break;
                    case AnnotationType.Mosaic:
                        {
                            var rect = SKRect.Create(
                                (float)Math.Min(p1.X, p2.X), 
                                (float)Math.Min(p1.Y, p2.Y), 
                                Math.Abs(p1.X - p2.X), 
                                Math.Abs(p1.Y - p2.Y));
                            
                            if (rect.Width <= 0 || rect.Height <= 0) break;

                            int cellSize = (int)(12 * visualScaling); // Scale mosaic cells
                            
                            canvas.Save();
                            canvas.ClipRect(rect);

                            for (float y = rect.Top; y < rect.Bottom; y += cellSize)
                            {
                                for (float x = rect.Left; x < rect.Right; x += cellSize)
                                {
                                    float cw = Math.Min(cellSize, rect.Right - x);
                                    float ch = Math.Min(cellSize, rect.Bottom - y);
                                    
                                    // Get average color of the cell from original bitmap
                                    // Simpler approach: sample the center pixel of the cell
                                    var sampleX = (int)(x + cw / 2);
                                    var sampleY = (int)(y + ch / 2);
                                    
                                    // Clamp sample points
                                    sampleX = Math.Clamp(sampleX, 0, bitmap.Width - 1);
                                    sampleY = Math.Clamp(sampleY, 0, bitmap.Height - 1);
                                    
                                    var color = bitmap.GetPixel(sampleX, sampleY);
                                    
                                    using var fillPaint = new SKPaint { Color = color, Style = SKPaintStyle.Fill };
                                    canvas.DrawRect(SKRect.Create(x, y, cw, ch), fillPaint);
                                }
                            }
                            canvas.Restore();
                        }
                        break;
                    case AnnotationType.Blur:
                        {
                            var rect = SKRect.Create(
                                (float)Math.Min(p1.X, p2.X), 
                                (float)Math.Min(p1.Y, p2.Y), 
                                Math.Abs(p1.X - p2.X), 
                                Math.Abs(p1.Y - p2.Y));

                            if (rect.Width <= 0 || rect.Height <= 0) break;

                            canvas.Save();
                            canvas.ClipRect(rect);
                            
                            // Apply blur by drawing the bitmap onto itself with a blur filter
                            // Scale blur sigma for visual consistency
                            float blurSigma = (float)(20 * visualScaling);
                            using var blurPaint = new SKPaint
                            {
                                ImageFilter = SKImageFilter.CreateBlur(blurSigma, blurSigma)
                            };
                            
                            // Draw the region from the bitmap into the canvas via a layer or temp image
                            // Actually, drawing the bitmap onto its own canvas with a blur filter is standard
                            canvas.DrawBitmap(bitmap, 0, 0, blurPaint);
                            
                            canvas.Restore();
                        }
                        break;
                }
            }
        }

        return bitmap;
    }

    private void DrawArrow(SKCanvas canvas, SKPoint p1, SKPoint p2, SKPaint paint, float scale)
    {
        canvas.DrawLine(p1, p2, paint);
        
        var angle = (float)Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
        var arrowSize = (15.0f * scale) + paint.StrokeWidth; // Scale arrow head size
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
                  
                  // Encode to PNG for clipboard
                  using var encodedData = image.Encode(SKEncodedImageFormat.Png, 100);
                  using var stream = encodedData.AsStream();
                  using var ms = new MemoryStream();
                  stream.CopyTo(ms);
                  ms.Position = 0;
                  
                  var avaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(ms);
                  
                  // Use new extension method way
                  await Avalonia.Input.Platform.ClipboardExtensions.SetBitmapAsync(clipboard, avaloniaBitmap);
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
            var clipboard = topLevel?.Clipboard;
            var storageProvider = topLevel?.StorageProvider;

            if (clipboard != null && storageProvider != null)
            {
                 var file = await storageProvider.TryGetFileFromPathAsync(new Uri(filePath));
                 if (file != null)
                 {
                     await Avalonia.Input.Platform.ClipboardExtensions.SetFilesAsync(clipboard, new[] { file });
                 }
                 System.Diagnostics.Debug.WriteLine($"Avalonia Clipboard: Copied file {filePath}");
            }
            else
            {
                 System.Diagnostics.Debug.WriteLine("Avalonia Clipboard: Clipboard not available");
            }
        });
    }
}
