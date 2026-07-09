using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Floating;

namespace GimmeCapture.Services.Core.Infrastructure;

public class ClipboardService : IClipboardService
{
    public async Task CopyImageAsync(Bitmap bitmap)
    {
        try
        {
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                // Windows-specific robust copy. The OLE SetDataObject write is SYNCHRONOUS and can wedge on a
                // large image racing the Windows clipboard-history listeners (or another app holding the clipboard
                // open) — running it on the UI thread froze the whole app. Do the encode + write on a dedicated
                // STA thread bounded by a timeout so the clipboard can never block the UI thread. SetDataObject
                // with copy:true flushes the data, so it persists after the worker thread exits.
                await StaClipboard.RunAsync(() =>
                {
                    var pngBytes = FloatingBitmapConversionHelper.EncodeBitmapToPngBytes(bitmap);

                    using var msForBitmap = new System.IO.MemoryStream(pngBytes);
                    using var winBitmap = new System.Drawing.Bitmap(msForBitmap);

                    // Create DataObject with multiple formats
                    var data = new System.Windows.Forms.DataObject();

                    // 1. Standard Bitmap (Legacy apps) - Alpha might be lost depending on app
                    data.SetData(System.Windows.Forms.DataFormats.Bitmap, true, winBitmap);

                    // 2. PNG Format (Modern apps: Chrome, Discord, Slack support transparency via this)
                    // Note: Stream must be kept open? DataObject usually serializes it.
                    // Ideally we pass MemoryStream.
                    using var pngStream = new System.IO.MemoryStream(pngBytes);
                    data.SetData("PNG", false, pngStream);

                    RetryClipboardWrite(() => System.Windows.Forms.Clipboard.SetDataObject(data, true));
                }, TimeSpan.FromSeconds(8), "Clipboard.CopyImage", "ClipboardSetImage").ConfigureAwait(false);
            }
            else
#endif
            {
                await CopyImageFallbackAsync(bitmap);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("Clipboard.CopyImage", ex);
        }
    }

    private async Task CopyImageFallbackAsync(Bitmap bitmap)
    {
        var topLevel = GetTopLevel();
        if (topLevel?.Clipboard is { } clipboard)
        {
            // Trying explicit extension method call
            await Avalonia.Input.Platform.ClipboardExtensions.SetBitmapAsync(clipboard, bitmap);
        }
    }

    public async Task CopyTextAsync(string text)
    {
        var topLevel = GetTopLevel();
        if (topLevel?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public async Task CopyFileAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            var fullPath = Path.GetFullPath(filePath);
            // SetFileDropList is a synchronous OLE write that can wedge when the clipboard is contended; run it on
            // a dedicated STA thread bounded by a timeout so it can never block the UI thread.
            await StaClipboard.RunAsync(() =>
            {
                var fileList = new System.Collections.Specialized.StringCollection();
                fileList.Add(fullPath);

                // Use WinForms for reliable file copy (standard Windows way)
                RetryClipboardWrite(() => System.Windows.Forms.Clipboard.SetFileDropList(fileList));
            }, TimeSpan.FromSeconds(8), "Clipboard.CopyFile", "ClipboardSetFiles").ConfigureAwait(false);
        }
        else
#endif
        {
            var topLevel = GetTopLevel();
            var clipboard = topLevel?.Clipboard;
            var storageProvider = topLevel?.StorageProvider;

            if (clipboard != null && storageProvider != null)
            {
                var file = await storageProvider.TryGetFileFromPathAsync(new Uri(filePath));
                if (file != null)
                {
                    await Avalonia.Input.Platform.ClipboardExtensions.SetFilesAsync(clipboard, new[] { file });
                }
            }
        }
    }

    public async Task CopyFileAndImageAsync(string filePath, Bitmap bitmap)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            await CopyImageAsync(bitmap);
            return;
        }

        try
        {
#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                var fullPath = Path.GetFullPath(filePath);
                // The OLE SetDataObject write is synchronous and can wedge when the clipboard is contended; run it
                // (plus the thumbnail encode) on a bounded STA thread so it can never block the UI thread.
                await StaClipboard.RunAsync(() =>
                {
                    // The FILE is the primary payload (e.g. a trimmed video clip) — set it first so
                    // it always lands on the clipboard. The thumbnail bitmap is best-effort: if it
                    // fails (e.g. a null/odd frame), we must NOT skip the file, or a stale prior
                    // clipboard entry (the whole video) would survive and paste instead.
                    var fileList = new System.Collections.Specialized.StringCollection();
                    fileList.Add(fullPath);

                    var data = new System.Windows.Forms.DataObject();
                    data.SetFileDropList(fileList);

                    try
                    {
                        var pngBytes = FloatingBitmapConversionHelper.EncodeBitmapToPngBytes(bitmap);
                        using var msForBitmap = new System.IO.MemoryStream(pngBytes);
                        using var winBitmap = new System.Drawing.Bitmap(msForBitmap);
                        data.SetData(System.Windows.Forms.DataFormats.Bitmap, true, winBitmap);
                        var pngStream = new System.IO.MemoryStream(pngBytes);
                        data.SetData("PNG", false, pngStream);
                    }
                    catch (Exception imgEx)
                    {
                        AppLog.Warning("Clipboard.CopyFileAndImage.Thumbnail", imgEx);
                    }

                    RetryClipboardWrite(() => System.Windows.Forms.Clipboard.SetDataObject(data, true));
                }, TimeSpan.FromSeconds(8), "Clipboard.CopyFileAndImage", "ClipboardSetFileAndImage").ConfigureAwait(false);
            }
            else
#endif
            {
                // Fallback: Copy file first, then image (the second will likely win on non-Windows)
                // or just copy image as it's the "richer" one for annotations
                await CopyImageAsync(bitmap);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("Clipboard.CopyFileAndImage", ex);
        }
    }

#if WINDOWS
    // Runs a flushing clipboard write with a short bounded retry for the transient "clipboard is locked by another
    // app" ExternalException. Rethrows the last error if every attempt fails so the STA runner reports the write
    // as failed rather than as a false success. Must be called on an STA thread (see StaClipboard).
    private static void RetryClipboardWrite(Action write)
    {
        System.Runtime.InteropServices.ExternalException? lastError = null;
        for (int i = 0; i < 5; i++)
        {
            try
            {
                write();
                return;
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                lastError = ex;
                System.Threading.Thread.Sleep(100);
            }
        }

        if (lastError != null)
        {
            throw lastError;
        }
    }
#endif

    private TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return TopLevel.GetTopLevel(desktop.MainWindow);
        }
        return null;
    }
}
