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
using System.Text;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using Avalonia.Controls;


using System.Collections.Generic;
using GimmeCapture.Models;
using Avalonia.Media;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.Services.Core.Rendering;

namespace GimmeCapture.Services.Platforms.Windows;

public class WindowsScreenCaptureService : IScreenCaptureService
{
    private readonly IWindowManager _windowManager;

    public WindowsScreenCaptureService(IWindowManager? windowManager = null)
    {
        _windowManager = windowManager ?? new AvaloniaWindowManager();
    }

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
                        catch (Exception ex) { AppLog.Warning("ScreenCapture.DrawCursor", ex); }
                    }
                }
                
                // Convert System.Drawing.Bitmap to SKBitmap via direct pixel copy
                // to avoid PNG encode/decode roundtrip allocations.
                var skBitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
                var bmpData = bitmap.LockBits(
                    new System.Drawing.Rectangle(0, 0, width, height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                try
                {
                    IntPtr dstPixels = skBitmap.GetPixels();
                    int dstStride = skBitmap.RowBytes;
                    int srcStride = bmpData.Stride;

                    unsafe
                    {
                        byte* srcBase = (byte*)bmpData.Scan0;
                        byte* dstBase = (byte*)dstPixels;
                        int copyBytesPerRow = width * 4;

                        for (int row = 0; row < height; row++)
                        {
                            var srcRow = srcBase + (row * srcStride);
                            var dstRow = dstBase + (row * dstStride);
                            Buffer.MemoryCopy(srcRow, dstRow, dstStride, copyBytesPerRow);
                        }
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bmpData);
                }

                return skBitmap;
            }
            
            return new SKBitmap(100, 100);
        });
    }

    public async Task<global::Avalonia.Media.Imaging.WriteableBitmap?> CaptureRegionBitmapAsync(global::Avalonia.Rect region, global::Avalonia.PixelPoint screenOffset, double visualScaling)
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
                var writeableBitmap = new global::Avalonia.Media.Imaging.WriteableBitmap(
                    new PixelSize(widthPhysical, heightPhysical), 
                    new Vector(96, 96), 
                    global::Avalonia.Platform.PixelFormat.Bgra8888, 
                    global::Avalonia.Platform.AlphaFormat.Premul);

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

    public async Task<SKBitmap> CaptureScreenWithAnnotationsAsync(Rect region, PixelPoint screenOffset, double visualScaling, 
        IEnumerable<Annotation> annotations, 
        IEnumerable<UserSelectionRect>? translationSelections = null,
        IEnumerable<TranslatedBlock>? translationBlocks = null,
        bool includeCursor = false)
    {
        var bitmap = await CaptureScreenAsync(region, screenOffset, visualScaling, includeCursor);
        // annotations can be null, but we check count later.
        
        // Use the visualScaling to adjust all logical coordinates to physical coordinates
        float scale = (float)visualScaling;

        using (var canvas = new SKCanvas(bitmap))
        {
            // 1. Render manual translation selections (UserSelectionRect)
            if (translationSelections != null)
            {
                foreach (var sel in translationSelections)
                {
                    if (!sel.IsTranslated || string.IsNullOrWhiteSpace(sel.TranslatedText)) continue;

                    // sel.Bounds is logical coordinate relative to the screen.
                    // We need to map it relative to the captured region.
                    var relBounds = new Rect(sel.Bounds.Position - region.Position, sel.Bounds.Size);
                    if (relBounds.Width <= 0 || relBounds.Height <= 0) continue;

                    DrawTranslationBox(canvas, relBounds, sel.TranslatedText, sel.DisplayFontSize > 0 ? sel.DisplayFontSize : sel.InferredFontSize, scale);
                }
            }

            // 2. Render automatic translation blocks (TranslatedBlock)
            if (translationBlocks != null)
            {
                foreach (var block in translationBlocks)
                {
                    if (string.IsNullOrWhiteSpace(block.TranslatedText)) continue;

                    var relBounds = new Rect(block.Bounds.Position - region.Position, block.Bounds.Size);
                    if (relBounds.Width <= 0 || relBounds.Height <= 0) continue;

                    DrawTranslationBox(canvas, relBounds, block.TranslatedText, block.InferredFontSize, scale);
                }
            }

            // 3. Render annotations through the shared renderer so preview and export stay aligned
            if (annotations != null && annotations.AsValueEnumerable().Any())
            {
                AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
                    bitmap,
                    annotations,
                    region.Width,
                    region.Height,
                    bitmap.Width,
                    bitmap.Height);
            }
        }

        return bitmap;
    }

    private void DrawTranslationBox(SKCanvas canvas, Rect relBounds, string text, double fontSize, float scale)
    {
        // UI matches: solid black translation text background, CornerRadius 4, Padding 6, Margin 4
        // The relBounds already represents the text region.
        
        float padding = 6.0f * scale;
        float cornerRadius = 4.0f * scale;
        
        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        // Text rendering setup
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        // Font setup
        var weight = SKFontStyleWeight.Normal;
        var style = new SKFontStyle(weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        using var typeface = SKTypeface.FromFamilyName("Microsoft JhengHei", style); // Match UI preference
        using var font = new SKFont(typeface, (float)(fontSize * scale));

        var lines = WrapText(text, (float)(relBounds.Width * scale - padding * 2), font);
        float lineHeight = font.Size * 1.5f;
        var boxRect = new SKRect(
            (float)(relBounds.X * scale),
            (float)(relBounds.Y * scale),
            (float)((relBounds.X + relBounds.Width) * scale),
            (float)((relBounds.Y + relBounds.Height) * scale));
        
        canvas.DrawRoundRect(boxRect, cornerRadius, cornerRadius, paint);

        float textX = boxRect.Left + padding;
        float textY = boxRect.Top + padding + font.Size;

        foreach (var line in lines)
        {
            canvas.DrawText(line, textX, textY, font, textPaint);
            textY += lineHeight;
        }
    }

    private List<string> WrapText(string text, float maxWidth, SKFont font)
    {
        var result = new List<string>();
        var paragraphs = text.Split('\n');
        foreach (var p in paragraphs)
        {
            if (string.IsNullOrWhiteSpace(p))
            {
                result.Add("");
                continue;
            }

            var words = p.Split(' '); // Simple space-based wrapping for Latin
            // For CJK, we might need character-based wrapping if there are no spaces
            
            var currentLine = new StringBuilder();
            foreach (var word in words)
            {
                var testLine = currentLine.Length == 0 ? word : currentLine + " " + word;
                if (font.MeasureText(testLine) <= maxWidth)
                {
                    currentLine.Append(currentLine.Length == 0 ? word : " " + word);
                }
                else
                {
                    if (currentLine.Length > 0)
                    {
                        result.Add(currentLine.ToString());
                        currentLine.Clear();
                        currentLine.Append(word);
                    }
                    else
                    {
                        // Single word is too wide, force break characters for CJK or just add it
                        result.Add(word);
                    }
                }
            }
            if (currentLine.Length > 0) result.Add(currentLine.ToString());
        }
        return result;
    }

    private void DrawArrow(SKCanvas canvas, SKPoint p1, SKPoint p2, SKPaint paint, float scale)
    {
        canvas.DrawLine(p1, p2, paint);

        var dx = p2.X - p1.X;
        var dy = p2.Y - p1.Y;
        var len = (float)Math.Sqrt((dx * dx) + (dy * dy));
        if (len <= 0.001f) return;

        var ux = dx / len;
        var uy = dy / len;
        var px = -uy;
        var py = ux;

        var headLength = Math.Clamp((8.0f * scale) + (paint.StrokeWidth * 1.4f), 8.0f * scale, 18.0f * scale);
        // Softer inner notch angle (less steep).
        var halfWidth = headLength * 0.36f;
        var notchDepth = headLength * 0.38f;

        var left = new SKPoint(
            p2.X - (ux * headLength) + (px * halfWidth),
            p2.Y - (uy * headLength) + (py * halfWidth));
        var right = new SKPoint(
            p2.X - (ux * headLength) - (px * halfWidth),
            p2.Y - (uy * headLength) - (py * halfWidth));
        var notch = new SKPoint(
            p2.X - (ux * notchDepth),
            p2.Y - (uy * notchDepth));

        paint.Style = SKPaintStyle.Fill;
        using var path = new SKPath();
        path.MoveTo(p2);
        path.LineTo(left);
        path.LineTo(notch);
        path.LineTo(right);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    public async Task CopyToClipboardAsync(SKBitmap bitmap)
    {
        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
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
              var topLevel = ResolveClipboardTopLevel();
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
                  
                  var avaloniaBitmap = new global::Avalonia.Media.Imaging.Bitmap(ms);
                  
                  // Use new extension method way
                  await global::Avalonia.Input.Platform.ClipboardExtensions.SetBitmapAsync(clipboard, avaloniaBitmap);
              }
        });
    }

    public async Task<bool> CopyToClipboardAsync(string text)
    {
        // WinForms Clipboard.SetText throws on null/empty — nothing to copy anyway.
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            // The OLE/WinForms clipboard write is SYNCHRONOUS and can block for a long time — a large payload
            // racing the Windows clipboard-history listeners (or another app holding the clipboard open) can
            // wedge it. Running it on the UI thread froze the whole app when OCR produced long text. Do it on a
            // dedicated STA thread bounded by a timeout so the clipboard can never block the UI thread; a
            // successful SetText flushes the data (copy:true) so it persists after the worker thread exits.
            return await TrySetClipboardTextStaAsync(text, TimeSpan.FromSeconds(4)).ConfigureAwait(false);
        }

        // Non-Windows: Avalonia's async clipboard.
        try
        {
            return await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var topLevel = ResolveClipboardTopLevel();
                if (topLevel?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(text);
                    return true;
                }

                return false;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warning("Clipboard.SetText.Fallback", ex);
            return false;
        }
    }

    // Write text to the clipboard on a dedicated STA thread, bounded by <paramref name="timeout"/>. Returns true
    // only if the write completed successfully in time. A thread that overruns the timeout is abandoned (it's a
    // background thread, so it never blocks the caller or app exit) rather than blocking the UI thread — which is
    // what caused the reported hang with long OCR text.
    private static Task<bool> TrySetClipboardTextStaAsync(string text, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                System.Windows.Forms.Clipboard.SetText(text);
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                AppLog.Warning("Clipboard.SetText.Sta", ex);
                tcs.TrySetResult(false);
            }
        })
        {
            IsBackground = true,
            Name = "ClipboardSetText",
        };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();

        return AwaitWithTimeoutAsync(tcs.Task, timeout);
    }

    private static async Task<bool> AwaitWithTimeoutAsync(Task<bool> task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            AppLog.Warning("Clipboard.SetText.Timeout", new TimeoutException($"Clipboard write exceeded {timeout.TotalSeconds:0}s"));
            return false;
        }

        return task.Result;
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
        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var topLevel = ResolveClipboardTopLevel();
            var clipboard = topLevel?.Clipboard;
            var storageProvider = topLevel?.StorageProvider;

            if (clipboard != null && storageProvider != null)
            {
                 var file = await storageProvider.TryGetFileFromPathAsync(new Uri(filePath));
                 if (file != null)
                 {
                     await global::Avalonia.Input.Platform.ClipboardExtensions.SetFilesAsync(clipboard, new[] { file });
                 }
                 System.Diagnostics.Debug.WriteLine($"Avalonia Clipboard: Copied file {filePath}");
            }
            else
            {
                 System.Diagnostics.Debug.WriteLine("Avalonia Clipboard: Clipboard not available");
            }
        });
    }

    private TopLevel? ResolveClipboardTopLevel()
    {
        var owner = _windowManager.GetActiveWindow() ?? _windowManager.GetMainWindow();
        return owner == null ? null : TopLevel.GetTopLevel(owner);
    }
}
