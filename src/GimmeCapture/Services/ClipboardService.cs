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

namespace GimmeCapture.Services;

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
                        using var ms = new System.IO.MemoryStream();
                        bitmap.Save(ms); // Saves as PNG by default
                        ms.Position = 0;
                        using var winBitmap = new System.Drawing.Bitmap(ms);
                        
                        // Specific retry logic for clipboard which can be locked
                        for (int i = 0; i < 5; i++)
                        {
                            try
                            {
                                System.Windows.Forms.Clipboard.SetImage(winBitmap);
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
                        System.Diagnostics.Debug.WriteLine($"WinForms Clipboard failed: {ex}");
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
            System.Diagnostics.Debug.WriteLine($"Failed to copy image: {ex}");
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

    private TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return TopLevel.GetTopLevel(desktop.MainWindow);    
        }
        return null;
    }
}
