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
            if (OperatingSystem.IsWindows())
            {
                // Windows-specific robust copy - MUST run on UI Thread (STA) for Clipboard
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                {
                    try 
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
                        
                        // Specific retry logic for clipboard which can be locked
                        for (int i = 0; i < 5; i++)
                        {
                            try
                            {
                                System.Windows.Forms.Clipboard.SetDataObject(data, true);
                                return;
                            }
                            catch (System.Runtime.InteropServices.ExternalException)
                            {
                                System.Threading.Thread.Sleep(100);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning("Clipboard.CopyImage.WinForms", ex);
                        // Continue to fallback if WinForms fails (though uncommon for basic copy)
                    }
                });
            }
            else
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

        if (OperatingSystem.IsWindows())
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
            {
                try
                {
                    var fileList = new System.Collections.Specialized.StringCollection();
                    fileList.Add(Path.GetFullPath(filePath));
                    
                    // Use WinForms for reliable file copy (standard Windows way)
                    for (int i = 0; i < 5; i++)
                    {
                        try
                        {
                            System.Windows.Forms.Clipboard.SetFileDropList(fileList);
                            return;
                        }
                        catch (System.Runtime.InteropServices.ExternalException)
                        {
                            System.Threading.Thread.Sleep(100);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warning("Clipboard.CopyFile.WinForms", ex);
                }
            });
        }
        else
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
            if (OperatingSystem.IsWindows())
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        // The FILE is the primary payload (e.g. a trimmed video clip) — set it first so
                        // it always lands on the clipboard. The thumbnail bitmap is best-effort: if it
                        // fails (e.g. a null/odd frame), we must NOT skip the file, or a stale prior
                        // clipboard entry (the whole video) would survive and paste instead.
                        var fileList = new System.Collections.Specialized.StringCollection();
                        fileList.Add(Path.GetFullPath(filePath));

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

                        for (int i = 0; i < 5; i++)
                        {
                            try
                            {
                                System.Windows.Forms.Clipboard.SetDataObject(data, true);
                                return;
                            }
                            catch (System.Runtime.InteropServices.ExternalException)
                            {
                                System.Threading.Thread.Sleep(100);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warning("Clipboard.CopyFileAndImage.WinForms", ex);
                    }
                });
            }
            else
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

    private TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return TopLevel.GetTopLevel(desktop.MainWindow);    
        }
        return null;
    }
}
