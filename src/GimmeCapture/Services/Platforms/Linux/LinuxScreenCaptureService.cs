using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Rendering;
using SkiaSharp;

namespace GimmeCapture.Services.Platforms.Linux;

/// <summary>
/// X11 <see cref="IScreenCaptureService"/> for Linux (docs/LINUX_PORT_FEASIBILITY.md, Phase 1).
/// Grabs a screen region with libX11 <c>XGetImage</c> on the root window, composites annotations
/// through the shared <see cref="AnnotationRenderService"/>, and routes clipboard/save through
/// Avalonia + SkiaSharp (all cross-platform). Cursor overlay and Wayland are not handled yet:
/// under Wayland an XWayland root grab typically yields a black image, so this targets X11 / XWayland.
/// </summary>
public sealed class LinuxScreenCaptureService : IScreenCaptureService
{
    public Task<SKBitmap> CaptureScreenAsync(Rect region, PixelPoint screenOffset, double visualScaling, bool includeCursor = false)
    {
        return Task.Run(() =>
        {
            var (x, y, width, height) = ToPhysicalRect(region, screenOffset, visualScaling);
            if (width <= 0 || height <= 0)
            {
                return new SKBitmap(1, 1);
            }

            try
            {
                return X11Grab(x, y, width, height) ?? new SKBitmap(width, height);
            }
            catch (Exception ex)
            {
                AppLog.Warning("LinuxScreenCapture.CaptureScreen", ex);
                return new SKBitmap(width, height);
            }
        });
    }

    public async Task<SKBitmap> CaptureScreenWithAnnotationsAsync(Rect region, PixelPoint screenOffset, double visualScaling,
        IEnumerable<Annotation> annotations,
        IEnumerable<UserSelectionRect>? translationSelections = null,
        IEnumerable<TranslatedBlock>? translationBlocks = null,
        bool includeCursor = false)
    {
        var bitmap = await CaptureScreenAsync(region, screenOffset, visualScaling, includeCursor).ConfigureAwait(false);

        // Annotations use the same Skia renderer as preview/export so the output stays aligned. Translation
        // overlays are a Translate-mode concern and are not composited on Linux yet (Phase 2).
        if (annotations != null && annotations.Any())
        {
            AnnotationRenderService.Shared.RenderAnnotationsToBitmap(
                bitmap,
                annotations,
                region.Width,
                region.Height,
                bitmap.Width,
                bitmap.Height);
        }

        return bitmap;
    }

    public Task<WriteableBitmap?> CaptureRegionBitmapAsync(Rect region, PixelPoint screenOffset, double visualScaling)
    {
        return Task.Run(() =>
        {
            var (x, y, width, height) = ToPhysicalRect(region, screenOffset, visualScaling);
            if (width <= 0 || height <= 0)
            {
                return (WriteableBitmap?)null;
            }

            try
            {
                using var skBitmap = X11Grab(x, y, width, height);
                if (skBitmap == null)
                {
                    return null;
                }

                var writeable = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    global::Avalonia.Platform.PixelFormat.Bgra8888,
                    global::Avalonia.Platform.AlphaFormat.Premul);

                using (var locked = writeable.Lock())
                {
                    int rowBytes = width * 4;
                    unsafe
                    {
                        byte* src = (byte*)skBitmap.GetPixels();
                        byte* dst = (byte*)locked.Address;
                        for (int row = 0; row < height; row++)
                        {
                            Buffer.MemoryCopy(src + (row * skBitmap.RowBytes), dst + (row * locked.RowBytes), locked.RowBytes, rowBytes);
                        }
                    }
                }

                return writeable;
            }
            catch (Exception ex)
            {
                AppLog.Warning("LinuxScreenCapture.CaptureRegionBitmap", ex);
                return null;
            }
        });
    }

    public async Task<bool> CopyToClipboardAsync(SKBitmap bitmap)
    {
        // Reports failure honestly: a silent miss here leaves the PREVIOUS clipboard content in place,
        // and the UI would otherwise claim "Copied" over a stale image.
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                if (ResolveTopLevel()?.Clipboard is not { } clipboard)
                {
                    AppLog.Warning("LinuxScreenCapture.CopyImage", "No TopLevel/clipboard available.");
                    return false;
                }

                using var image = SKImage.FromBitmap(bitmap);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = data.AsStream();
                var avaloniaBitmap = new global::Avalonia.Media.Imaging.Bitmap(stream);
                await global::Avalonia.Input.Platform.ClipboardExtensions.SetBitmapAsync(clipboard, avaloniaBitmap);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warning("LinuxScreenCapture.CopyImage", ex);
                return false;
            }
        });
    }

    public async Task<bool> CopyToClipboardAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (ResolveTopLevel()?.Clipboard is { } clipboard)
            {
                await global::Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
                return true;
            }

            return false;
        });
    }

    public async Task CopyFileToClipboardAsync(string filePath)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var topLevel = ResolveTopLevel();
            var clipboard = topLevel?.Clipboard;
            var storageProvider = topLevel?.StorageProvider;
            if (clipboard == null || storageProvider == null)
            {
                return;
            }

            var file = await storageProvider.TryGetFileFromPathAsync(new Uri(filePath));
            if (file != null)
            {
                await global::Avalonia.Input.Platform.ClipboardExtensions.SetFilesAsync(clipboard, new[] { file });
            }
        });
    }

    public Task SaveToFileAsync(SKBitmap bitmap, string path)
    {
        // OS-agnostic (SkiaSharp).
        return Task.Run(() =>
        {
            using var stream = System.IO.File.OpenWrite(path);
            bitmap.Encode(stream, SKEncodedImageFormat.Png, 100);
        });
    }

    private static (int X, int Y, int Width, int Height) ToPhysicalRect(Rect region, PixelPoint screenOffset, double visualScaling)
    {
        int x = (int)(region.X * visualScaling) + screenOffset.X;
        int y = (int)(region.Y * visualScaling) + screenOffset.Y;
        int width = (int)(region.Width * visualScaling);
        int height = (int)(region.Height * visualScaling);
        return (x, y, width, height);
    }

    private static TopLevel? ResolveTopLevel()
    {
        // Application.Current is acceptable inside an Avalonia platform service implementation.
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
            return window == null ? null : TopLevel.GetTopLevel(window);
        }

        return null;
    }

    // ---- X11 capture -------------------------------------------------------------------------

    private const int ZPixmap = 2;

    private static SKBitmap? X11Grab(int x, int y, int width, int height)
    {
        IntPtr display = XOpenDisplay(null);
        if (display == IntPtr.Zero)
        {
            AppLog.Warning("LinuxScreenCapture.X11Grab", new InvalidOperationException("XOpenDisplay returned null (no X11 display; Wayland-only session?)."));
            return null;
        }

        try
        {
            IntPtr root = XDefaultRootWindow(display);

            // Clamp to the root geometry — XGetImage raises an X protocol error (which aborts the process
            // via the default Xlib handler) if the rectangle extends outside the drawable.
            if (XGetGeometry(display, root, out _, out _, out _, out uint rootW, out uint rootH, out _, out _) != 0)
            {
                if (x < 0) { width += x; x = 0; }
                if (y < 0) { height += y; y = 0; }
                if (x + width > (int)rootW) { width = (int)rootW - x; }
                if (y + height > (int)rootH) { height = (int)rootH - y; }
                if (width <= 0 || height <= 0)
                {
                    return null;
                }
            }

            IntPtr imgPtr = XGetImage(display, root, x, y, (uint)width, (uint)height, ~UIntPtr.Zero, ZPixmap);
            if (imgPtr == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var img = Marshal.PtrToStructure<XImage>(imgPtr);
                return ConvertToSkBitmap(img, width, height);
            }
            finally
            {
                DestroyXImage(imgPtr);
            }
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static unsafe SKBitmap ConvertToSkBitmap(XImage img, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

        ulong redMask = (ulong)img.red_mask;
        ulong greenMask = (ulong)img.green_mask;
        ulong blueMask = (ulong)img.blue_mask;
        int redShift = TrailingZeros(redMask);
        int greenShift = TrailingZeros(greenMask);
        int blueShift = TrailingZeros(blueMask);
        int bytesPerPixel = img.bits_per_pixel / 8;

        byte* src = (byte*)img.data;
        byte* dst = (byte*)bitmap.GetPixels();
        int dstStride = bitmap.RowBytes;

        for (int row = 0; row < height; row++)
        {
            byte* srcRow = src + ((long)row * img.bytes_per_line);
            byte* dstRow = dst + ((long)row * dstStride);
            for (int col = 0; col < width; col++)
            {
                byte* px = srcRow + (col * bytesPerPixel);
                ulong pixel = bytesPerPixel >= 4
                    ? *(uint*)px
                    : (ulong)(px[0] | (px[1] << 8) | (px[2] << 16));

                byte* outPx = dstRow + (col * 4);
                outPx[0] = (byte)((pixel & blueMask) >> blueShift);   // B
                outPx[1] = (byte)((pixel & greenMask) >> greenShift); // G
                outPx[2] = (byte)((pixel & redMask) >> redShift);     // R
                outPx[3] = 0xFF;                                       // opaque
            }
        }

        return bitmap;
    }

    private static int TrailingZeros(ulong mask)
    {
        if (mask == 0)
        {
            return 0;
        }

        int count = 0;
        while ((mask & 1) == 0)
        {
            mask >>= 1;
            count++;
        }

        return count;
    }

    private static void DestroyXImage(IntPtr imgPtr)
    {
        // XDestroyImage is a macro that calls ximage->f.destroy_image(ximage); invoke that function pointer.
        var img = Marshal.PtrToStructure<XImage>(imgPtr);
        if (img.destroy_image != IntPtr.Zero)
        {
            var destroy = Marshal.GetDelegateForFunctionPointer<XDestroyImageDelegate>(img.destroy_image);
            destroy(imgPtr);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XDestroyImageDelegate(IntPtr image);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(string? displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XDefaultRootWindow(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y, uint width, uint height, UIntPtr planeMask, int format);

    [DllImport("libX11.so.6")]
    private static extern int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y, out uint width, out uint height, out uint borderWidth, out uint depth);

    // Field order matches Xlib's XImage; sequential layout with natural x86-64 alignment maps 1:1 to the C ABI.
    [StructLayout(LayoutKind.Sequential)]
    private struct XImage
    {
        public int width;
        public int height;
        public int xoffset;
        public int format;
        public IntPtr data;
        public int byte_order;
        public int bitmap_unit;
        public int bitmap_bit_order;
        public int bitmap_pad;
        public int depth;
        public int bytes_per_line;
        public int bits_per_pixel;
        public UIntPtr red_mask;
        public UIntPtr green_mask;
        public UIntPtr blue_mask;
        public IntPtr obdata;
        // struct funcs (inlined function pointers, in declaration order)
        public IntPtr create_image;
        public IntPtr destroy_image;
        public IntPtr get_pixel;
        public IntPtr put_pixel;
        public IntPtr sub_image;
        public IntPtr add_pixel;
    }
}
