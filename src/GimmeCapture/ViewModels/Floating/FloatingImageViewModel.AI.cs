using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Collections.Generic;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using System.Linq;
using System.Reactive.Linq;
using System;
using System.Threading.Tasks;
using GimmeCapture.ViewModels.Shared;
using GimmeCapture.Views.Floating;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingImageViewModel
{
    private void InitializeAICommands()
    {
        // _canRemoveBackground is initialized in main constructor
    }

    private async Task StartInteractiveRemovalAsync()
    {
        System.Console.WriteLine("[FloatingVM] StartInteractiveRemovalAsync Entered");
        if (CurrentTool != FloatingTool.PointRemoval) 
        {
             System.Console.WriteLine($"[FloatingVM] CurrentTool is {CurrentTool}, not PointRemoval. Aborting.");
             return;
        }

        // Check if AI is enabled
        if (!_appSettingsService.Settings.EnableAI)
        {
            System.Console.WriteLine("[FloatingVM] AI Disabled settings check failed.");
            DiagnosticText = LocalizationService.Instance["AIDisabled"];
            CurrentTool = FloatingTool.None;

            ShowDialogOnUiThread(
                LocalizationService.Instance["AIDisabledTitle"],
                LocalizationService.Instance["AIDisabledMessage"]);
            return;
        }

        System.Console.WriteLine("[FloatingVM] Getting SAM2 Service...");
        
        IsProcessing = true;
        ProcessingText = LocalizationService.Instance["StatusInitializingAI"];
        
        var sam2 = await GetSAM2ServiceAsync();
        if (sam2 == null) 
        {
            System.Console.WriteLine("[FloatingVM] SAM2 Service returned null.");
            IsProcessing = false;
            IsPointRemovalMode = false;
            return;
        }

        try
        {
            System.Console.WriteLine("[FloatingVM] SAM2 Service Ready. Setting up Interactive Mode.");
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["StatusInitializingAI"];
            
            // Image is already set by GetSAM2ServiceAsync using direct SKBitmap conversion
            
            // Reset points list
            _interactivePoints.Clear();
            IsInteractiveSelectionMode = true; 
            
            DiagnosticText = $"{LocalizationService.Instance["StatusReady"]} [{sam2.ModelVariantName}]";
            System.Console.WriteLine("[FloatingVM] Interactive Selection Ready");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FloatingVM: Failed to start interactive removal: {ex}");
            DiagnosticText = LocalizationService.Instance["StatusError"]; // Or specify a new one
            CurrentTool = FloatingTool.None;

            var initMessage = string.Format(LocalizationService.Instance["AIInitErrorMessage"], ex.Message);
            ShowDialogOnUiThread(LocalizationService.Instance["AIInitErrorTitle"], initMessage);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public void ResetInteractivePoints()
    {
        _interactivePoints.Clear();
        _invertSelectionMode = false;
        InteractiveMask = null;
        _cleanMaskBytes = null;
        DiagnosticText = "AI: Points Reset";
        System.Diagnostics.Debug.WriteLine("FloatingVM: Resetting interactive points");
    }

    public async Task UndoLastPointAsync()
    {
        if (_interactivePoints.Count > 0)
        {
            _interactivePoints.RemoveAt(_interactivePoints.Count - 1);
            
            // CRITICAL: Reset the AI's mask feedback memory when undoing.
            // If the last result was a "bad" full-image mask, we don't want the AI to reuse it.

            if (_interactivePoints.Count == 0)
            {
                ResetInteractivePoints();
            }
            else
            {
                await RefineMaskAsync();
            }
        }
    }

    // Synchronous wrapper for right-click undo
    public void UndoLastInteractivePoint()
    {
        _ = UndoLastPointAsync();
    }

    private async Task RefineMaskAsync()
    {
        // Check Resources and download if needed (SAM2)
        if (!await EnsureAIResourcesAsync()) return;

        var sam2 = await GetSAM2ServiceAsync();
        if (sam2 == null) return;
    
        DiagnosticText = "AI: Refining...";
        try
        {
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["StatusProcessing"];
            
            var maskBytes = await sam2.GetMaskAsync(_interactivePoints);
            var iouInfo = sam2.LastIouInfo;
            DiagnosticText = $"AI: ({_interactivePoints.Count} pts) {iouInfo}";

            if (maskBytes != null && maskBytes.Length > 0)
            {
                // Store clean mask for actual removal (without crosshairs)
                _cleanMaskBytes = maskBytes;
                
                using var grayMask = SkiaSharp.SKBitmap.Decode(maskBytes);
                
                // CRITICAL FIX: Convert grayscale mask to RGBA with transparency
                // Color based on mode: Red (remove) vs Green (keep)
                SkiaSharp.SKColor overlayColor = _invertSelectionMode 
                    ? new SkiaSharp.SKColor(0, 255, 100, 150)   // Green for "Keep mode" (Shift+Click)
                    : new SkiaSharp.SKColor(255, 80, 80, 150);  // Red for "Remove mode" (Normal)
                    
                using var coloredMask = new SkiaSharp.SKBitmap(grayMask.Width, grayMask.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
                for (int y = 0; y < grayMask.Height; y++)
                {
                    for (int x = 0; x < grayMask.Width; x++)
                    {
                        var grayVal = grayMask.GetPixel(x, y).Red; // Grayscale: R=G=B
                        if (grayVal > 127)
                        {
                            // Selected area with mode-specific color
                            coloredMask.SetPixel(x, y, overlayColor);
                        }
                        else
                        {
                            // Unselected area: Fully transparent
                            coloredMask.SetPixel(x, y, SkiaSharp.SKColors.Transparent);
                        }
                    }
                }
                
                using (var canvas = new SkiaSharp.SKCanvas(coloredMask))
                {
                    var posPaint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.LimeGreen, Style = SkiaSharp.SKPaintStyle.Fill, IsAntialias = true };
                    var negPaint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Red, Style = SkiaSharp.SKPaintStyle.Fill, IsAntialias = true };
                    
                    // Scale points to match the mask bitmap size
                    float scaleX = (float)coloredMask.Width / (Image?.PixelSize.Width ?? 1);
                    float scaleY = (float)coloredMask.Height / (Image?.PixelSize.Height ?? 1);

                    foreach (var pt in _interactivePoints)
                    {
                        var px = (float)pt.X * scaleX;
                        var py = (float)pt.Y * scaleY;
                        
                        // Draw point circle
                        canvas.DrawCircle(px, py, 6, pt.IsPositive ? posPaint : negPaint);
                        
                        // DRAW CALIBRATION CROSSHAIR
                        using var crossPaint = new SkiaSharp.SKPaint { 
                            Color = pt.IsPositive ? SkiaSharp.SKColors.Lime : SkiaSharp.SKColors.DeepPink, 
                            StrokeWidth = 2, 
                            Style = SkiaSharp.SKPaintStyle.Stroke,
                            IsAntialias = true
                        };
                        canvas.DrawLine(px - 20, py, px + 20, py, crossPaint);
                        canvas.DrawLine(px, py - 20, px, py + 20, crossPaint);
                        
                        // Draw a tiny center dot
                        using var dotPaint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Black.WithAlpha(180), Style = SkiaSharp.SKPaintStyle.Fill, IsAntialias = true };
                        canvas.DrawCircle(px, py, 1.5f, dotPaint);
                    }
                }

                using var finalMs = new System.IO.MemoryStream();
                coloredMask.Encode(finalMs, SkiaSharp.SKEncodedImageFormat.Png, 100);
                InteractiveMask = FloatingBitmapConversionHelper.CreateDetachedBitmapFromEncodedBytes(finalMs.ToArray());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FloatingVM: RefineMask Error: {ex}");
            DiagnosticText = $"Refine Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public async Task HandlePointClickAsync(double x, double y, bool isPositive = true)
    {
        if (IsProcessing) return;
        
        // LOG PHYSICAL PIXEL COORDINATES FOR USER VERIFICATION
        System.Diagnostics.Debug.WriteLine($"[AI DEBUG] Click Pixel: ({x:F0}, {y:F0}) Type: {(isPositive ? "Positive" : "Negative")}");
        
        var physicalX = x;
        var physicalY = y;

        if (_sam2Service == null || !IsInteractiveSelectionMode) return;

        try
        {
            // First point determines the mode:
            // - Positive (normal click) = Remove selected area -> Red
            // - Negative (Shift+click) = Keep selected area -> Green
            bool sam2Positive = isPositive;
            if (_interactivePoints.Count == 0)
            {
                // _invertSelectionMode=true means "keep selected" mode in confirm stage.
                // So we invert the default click semantics here:
                // normal click -> remove selected (invert=false)
                // Shift+click -> keep selected (invert=true)
                _invertSelectionMode = !isPositive;
                sam2Positive = true; // First point always defines the subject for SAM2
                System.Diagnostics.Debug.WriteLine($"[AI MODE] First point. Keep selected mode = {_invertSelectionMode}");
            }
            
            _interactivePoints.Add((physicalX, physicalY, sam2Positive));

            await RefineMaskAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FloatingVM: Multi-point Error: {ex}");
            DiagnosticText = $"Click Error: {ex.Message}";
        }
    }

    private async Task<bool> ShowDownloadConfirmationAsync()
    {
        var msg = LocalizationService.Instance["AIDownloadConfirm"] ?? "Interactive AI Selection requires additional modules. Download now?";
        bool confirmed = false;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var owner = ResolveOwnerWindow();
            if (owner != null)
            {
                confirmed = await GimmeCapture.Views.Dialogs.UpdateDialog.ShowDialog(owner, msg, isUpdateAvailable: true);
            }
        });
        
        return confirmed;
    }

    public async Task<bool> EnsureAIResourcesAsync()
    {
        // 1. Check if already ready - Fast path
        var variant = _appSettingsService.Settings.SelectedSAM2Variant;
        if (_aiResourceService.IsAICoreReady() && _aiResourceService.IsSAM2Ready(variant)) return true;

        // 2. Check if already downloading (Background)
        var currentStatus = ResourceQueueService.Instance.GetStatus("AI");
        if (currentStatus == QueueItemStatus.Pending || currentStatus == QueueItemStatus.Downloading)
        {
            ShowDialogOnUiThread(
                "Download in Progress",
                LocalizationService.Instance["ComponentDownloadingProgress"] ?? "Downloading component...");
            return false;
        }

        // 3. Not ready, Not downloading -> Ask for permission
        var confirmed = await ShowDownloadConfirmationAsync();
        if (!confirmed) return false;

        // 4. Start Download (Fire and Forget from UI perspective)
        _ = ResourceQueueService.Instance.EnqueueAsync("AI", async () =>
        {
             // Download Core and Selected Variant
             bool coreReady = await _aiResourceService.EnsureAICoreAsync();
             if (!coreReady) return false;
             
             var variant = _appSettingsService.Settings.SelectedSAM2Variant;
             return await _aiResourceService.EnsureSAM2Async(variant);
        });

        return false;
    }

    private async Task DownloadAIResourcesAsync()
    {
        if (await EnsureAIResourcesAsync())
        {
            CurrentTool = FloatingTool.PointRemoval;
            this.RaisePropertyChanged(nameof(IsPointRemovalMode));
        }
    }

    private async Task<bool> EnsureAICoreResourcesAsync()
    {
        // 1. Check if already ready - Fast path
        if (_aiResourceService.IsAICoreReady()) return true;

        // 2. Check if already downloading (Background)
        var currentStatus = ResourceQueueService.Instance.GetStatus("AI Core");
        if (currentStatus == QueueItemStatus.Pending || currentStatus == QueueItemStatus.Downloading)
        {
            ShowDialogOnUiThread(
                "Download in Progress",
                "AI Core components are currently downloading...");
            return false;
        }

        // 3. Not ready, Not downloading -> Ask for permission
        var msg = LocalizationService.Instance["AIDownloadConfirm"] ?? "Background Removal requires additional modules. Download now?";
        bool confirmed = false;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var owner = ResolveOwnerWindow();
             if (owner != null)
            {
                confirmed = await GimmeCapture.Views.Dialogs.UpdateDialog.ShowDialog(owner, msg, isUpdateAvailable: true);
            }
        });
        
        if (!confirmed) return false;

        // 4. Start Download
        _ = ResourceQueueService.Instance.EnqueueAsync("AI Core", async () =>
        {
             return await _aiResourceService.EnsureAICoreAsync();
        });

        return false;
    }

    private async Task RemoveBackgroundAsync()
    {
        if (Image == null) return;
        
        // Check if AI is enabled
        if (!_appSettingsService.Settings.EnableAI)
        {
            ShowDialogOnUiThread("AI Disabled", "AI features are currently disabled in Settings.");
            return;
        }
        
        // Check Resources and download if needed (Core only, not SAM2)
        if (!await EnsureAICoreResourcesAsync()) return;

        try
        {
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["ProcessingAI"] ?? "Processing...";
            
            // Save state for Undo
            PushUndoState();

            // 1. Convert Avalonia Bitmap to Bytes
            if (!FloatingBitmapConversionHelper.TryEncodeBitmapToPngBytes(Image, out var imageBytes, out var encodeError))
                throw new Exception(encodeError ?? "Failed to serialize image.");

            // 2. Process
            using var aiService = new BackgroundRemovalService(_aiResourceService, _pathService);
            
            // SelectionRect is in logical pixels (UI space). 
            // We need to scale it to physical image pixels for BackgroundRemovalService.
            Avalonia.Rect? scaledRect = null;
            if (IsSelectionActive)
            {
                // Must use current DisplayWidth/Height for scaling the UI selection to physical pixels
                var refW = DisplayWidth > 0 ? DisplayWidth : OriginalWidth;
                var refH = DisplayHeight > 0 ? DisplayHeight : OriginalHeight;
                var scaleX = (double)Image.PixelSize.Width / refW;
                var scaleY = (double)Image.PixelSize.Height / refH;
                scaledRect = new Avalonia.Rect(
                    SelectionRect.X * scaleX,
                    SelectionRect.Y * scaleY,
                    SelectionRect.Width * scaleX,
                    SelectionRect.Height * scaleY);
            }

            var transparentBytes = await aiService.RemoveBackgroundAsync(imageBytes, scaledRect);

            // 3. Update Image
            if (!FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromEncodedBytes(transparentBytes, out var detachedBitmap, out var decodeError))
                throw new Exception(decodeError ?? "Failed to decode processed image.");
            Image = detachedBitmap;
            
            // Clear selection after processing
            IsSelectionMode = false;
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AI Processing Failed: {ex}");
            ShowErrorDialog(ex.Message);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task ConfirmInteractiveAsync()
    {
        if (Image == null || InteractiveMask == null) return;

        try
        {
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["ProcessingAI"] ?? "Applying Removal...";
            
            PushUndoState();

            var sourceImage = Image;
            var cleanMaskBytes = _cleanMaskBytes;
            if (sourceImage == null) throw new Exception("Source image is unavailable.");
            if (cleanMaskBytes == null || cleanMaskBytes.Length == 0) throw new Exception("No valid mask generated.");

            if (!FloatingBitmapConversionHelper.TryEncodeBitmapToPngBytes(sourceImage, out var sourceImageBytes, out var sourceEncodeError))
                throw new Exception(sourceEncodeError ?? "Failed to serialize source image.");

            // 1. Process with SkiaSharp in a background thread to prevent UI freeze
            var imageBytes = await Task.Run(() =>
            {
                using var originalBmp = SkiaSharp.SKBitmap.Decode(sourceImageBytes);
                if (originalBmp == null) throw new Exception("Failed to decode source image.");

                // Use CLEAN mask without crosshairs!
                using var maskBmp = SkiaSharp.SKBitmap.Decode(cleanMaskBytes);
                if (maskBmp == null) throw new Exception("Failed to decode interactive mask.");

                using var resultBmp = new SkiaSharp.SKBitmap(originalBmp.Width, originalBmp.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
                var maskScaleX = (double)maskBmp.Width / originalBmp.Width;
                var maskScaleY = (double)maskBmp.Height / originalBmp.Height;
                
                for (int y = 0; y < originalBmp.Height; y++)
                {
                    for (int x = 0; x < originalBmp.Width; x++)
                    {
                        var color = originalBmp.GetPixel(x, y);
                        var maskX = Math.Clamp((int)(x * maskScaleX), 0, maskBmp.Width - 1);
                        var maskY = Math.Clamp((int)(y * maskScaleY), 0, maskBmp.Height - 1);
                        var maskColor = maskBmp.GetPixel(maskX, maskY);
                        
                        // Apply mask based on mode:
                        // Normal mode: Selected = REMOVE, Unselected = KEEP
                        // Invert mode: Selected = KEEP, Unselected = REMOVE
                        var maskVal = maskColor.Red; // For Gray8, R=G=B=value
                        bool isSelected = maskVal > 127;
                        
                        // Invert the selection if in invert mode
                        if (_invertSelectionMode)
                        {
                            isSelected = !isSelected;
                        }
                        
                        byte alpha;
                        if (isSelected)
                        {
                            // This pixel is in the "remove" zone - make transparent
                            alpha = 0;
                        }
                        else
                        {
                            // This pixel is in the "keep" zone - preserve original
                            alpha = color.Alpha;
                        }
                        
                        resultBmp.SetPixel(x, y, new SkiaSharp.SKColor(color.Red, color.Green, color.Blue, alpha));
                    }
                }

                using var image = SkiaSharp.SKImage.FromBitmap(resultBmp);
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                return data.ToArray();
            });

            if (!FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromEncodedBytes(imageBytes, out var confirmedBitmap, out var confirmDecodeError))
                throw new Exception(confirmDecodeError ?? "Failed to decode interactive output.");
            Image = confirmedBitmap;

            IsPointRemovalMode = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to confirm interactive: {ex}");
            ShowErrorDialog($"Failed to apply background removal: {ex.Message}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task<SAM2Service?> GetSAM2ServiceAsync()
    {
        if (_sam2Service != null && _sam2Service.ModelVariantName != "Unknown") return _sam2Service;

        _sam2Service = new SAM2Service(_aiResourceService, _appSettingsService);
        // ProcessingText is handled by caller
        try
        {
            await _sam2Service.InitializeAsync();
            
             // Optimization: Pass current image to AI immediately after initialization
            var skImage = FloatingBitmapConversionHelper.ToSkBitmap(Image);
            if (skImage != null)
            {
                await _sam2Service.SetImageAsync(skImage);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AI] Init Failed: {ex.Message}");
            _sam2Service = null;
        }
        // Finally block removed - caller handles IsProcessing
        return _sam2Service;
    }

    private Avalonia.Controls.Window? ResolveOwnerWindow()
    {
        return _windowManager.FindWindowByDataContext(this)
            ?? _windowManager.GetActiveWindowOfType<FloatingImageWindow>()
            ?? _windowManager.GetActiveWindow()
            ?? _windowManager.GetMainWindow();
    }

    private void ShowErrorDialog(string message)
    {
        ShowDialogOnUiThread("Error", message);
    }

    private void ShowDialogOnUiThread(string title, string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var owner = ResolveOwnerWindow();
            if (owner == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Dialog] Owner not found. Title={title}, Message={message}");
                return;
            }

            var dialogVm = new GothicDialogViewModel
            {
                Title = title,
                Message = message
            };
            var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };
            dialog.ShowDialog<bool>(owner);
        });
    }
}
