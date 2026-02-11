using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.ViewModels.Shared;
using System;
using SkiaSharp;
using Avalonia.Threading;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingImageViewModel
{
    private SAM2Service? _sam2Service;
    private readonly List<(double X, double Y, bool IsPositive)> _interactivePoints = new();
    private bool _invertSelectionMode = false; // Shift+Click sets this to true
    
    // Clean mask without crosshairs for actual removal
    private byte[]? _cleanMaskBytes;

    private bool _isInteractiveSelectionMode;
    public bool IsInteractiveSelectionMode
    {
        get => _isInteractiveSelectionMode;
        set => this.RaiseAndSetIfChanged(ref _isInteractiveSelectionMode, value);
    }

    private Bitmap? _interactiveMask;
    public Bitmap? InteractiveMask
    {
        get => _interactiveMask;
        set => this.RaiseAndSetIfChanged(ref _interactiveMask, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set => this.RaiseAndSetIfChanged(ref _isProcessing, value);
    }

    private string _processingText = LocalizationService.Instance["StatusProcessing"];
    public string ProcessingText
    {
        get => _processingText;
        set => this.RaiseAndSetIfChanged(ref _processingText, value);
    }

    private string _diagnosticText = "Ready";
    public string DiagnosticText
    {
        get => _diagnosticText;
        set => this.RaiseAndSetIfChanged(ref _diagnosticText, value);
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set => this.RaiseAndSetIfChanged(ref _progressValue, value);
    }
    
    private bool _isIndeterminate = true;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => this.RaiseAndSetIfChanged(ref _isIndeterminate, value);
    }

    // Only allow background removal if not processing.
    // We could also check if already transparent, but that's harder to detect cheaply.
    private readonly ObservableAsPropertyHelper<bool> _canRemoveBackground;
    public bool CanRemoveBackground => _canRemoveBackground.Value;

    public ReactiveCommand<Unit, Unit> RemoveBackgroundCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmInteractiveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelInteractiveCommand { get; }

    private void InitializeAICommands()
    {
        // _canRemoveBackground is initialized in main constructor
    }

    private async Task StartInteractiveRemovalAsync()
    {
        if (CurrentTool != FloatingTool.PointRemoval) return;

        // Check if AI is enabled
        if (!_appSettingsService.Settings.EnableAI)
        {
            DiagnosticText = LocalizationService.Instance["AIDisabled"];
            CurrentTool = FloatingTool.None;
            
             Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 var dialogVm = new GothicDialogViewModel { 
                     Title = LocalizationService.Instance["AIDisabledTitle"], 
                     Message = LocalizationService.Instance["AIDisabledMessage"] 
                 };
                 var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };
                 
                 var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                 var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this) as Avalonia.Controls.Window;
                 
                  // Fallback: If owner specific to this VM not found, try any active FloatingImageWindow
                 if (owner == null)
                 {
                     owner = desktop?.Windows.OfType<GimmeCapture.Views.Floating.FloatingImageWindow>().FirstOrDefault(w => w.IsActive);
                 }
                 
                 // Final Fallback: Try Main Window or any active window
                 if (owner == null)
                 {
                     owner = desktop?.Windows.FirstOrDefault(w => w.IsActive) ?? desktop?.MainWindow;
                 }
                 
                 if (owner != null) 
                 {
                     dialog.ShowDialog<bool>(owner);
                 }
            });
            return;
        }

        var sam2 = await GetSAM2ServiceAsync();
        if (sam2 == null) return;

        try
        {
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["StatusInitializingAI"];
            
            // Image is already set by GetSAM2ServiceAsync using direct SKBitmap conversion
            
            // Reset points list
            _interactivePoints.Clear();
            IsInteractiveSelectionMode = true; 
            
            DiagnosticText = $"{LocalizationService.Instance["StatusReady"]} [{sam2.ModelVariantName}]";
            System.Diagnostics.Debug.WriteLine("FloatingVM: Interactive Selection Ready");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FloatingVM: Failed to start interactive removal: {ex}");
            DiagnosticText = LocalizationService.Instance["StatusError"]; // Or specify a new one
            CurrentTool = FloatingTool.None;
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 var dialogVm = new GothicDialogViewModel { 
                     Title = LocalizationService.Instance["AIInitErrorTitle"], 
                     Message = string.Format(LocalizationService.Instance["AIInitErrorMessage"], ex.Message)
                 };
                 var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };
                 var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                 var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this) as Avalonia.Controls.Window;
                 if (owner != null) dialog.ShowDialog<bool>(owner);
            });
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public void ResetInteractivePoints()
    {
        _interactivePoints.Clear();
        InteractiveMask = null;
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
                
                using var grayMask = SKBitmap.Decode(maskBytes);
                
                // CRITICAL FIX: Convert grayscale mask to RGBA with transparency
                // Color based on mode: Red (remove) vs Green (keep)
                SKColor overlayColor = _invertSelectionMode 
                    ? new SKColor(0, 255, 100, 150)   // Green for "Keep mode" (Shift+Click)
                    : new SKColor(255, 80, 80, 150);  // Red for "Remove mode" (Normal)
                    
                using var coloredMask = new SKBitmap(grayMask.Width, grayMask.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
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
                            coloredMask.SetPixel(x, y, SKColors.Transparent);
                        }
                    }
                }
                
                using (var canvas = new SKCanvas(coloredMask))
                {
                    var posPaint = new SKPaint { Color = SKColors.LimeGreen, Style = SKPaintStyle.Fill, IsAntialias = true };
                    var negPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill, IsAntialias = true };
                    
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
                        using var crossPaint = new SKPaint { 
                            Color = pt.IsPositive ? SKColors.Lime : SKColors.DeepPink, 
                            StrokeWidth = 2, 
                            Style = SKPaintStyle.Stroke,
                            IsAntialias = true
                        };
                        canvas.DrawLine(px - 20, py, px + 20, py, crossPaint);
                        canvas.DrawLine(px, py - 20, px, py + 20, crossPaint);
                        
                        // Draw a tiny center dot
                        using var dotPaint = new SKPaint { Color = SKColors.Black.WithAlpha(180), Style = SKPaintStyle.Fill, IsAntialias = true };
                        canvas.DrawCircle(px, py, 1.5f, dotPaint);
                    }
                }

                using var finalMs = new System.IO.MemoryStream();
                coloredMask.Encode(finalMs, SKEncodedImageFormat.Png, 100);
                finalMs.Seek(0, System.IO.SeekOrigin.Begin);
                InteractiveMask = new Bitmap(finalMs);
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
            // - Positive (normal click) = Remove selected area
            // - Negative (Shift+click) = Keep selected area (invert result)
            if (_interactivePoints.Count == 0)
            {
                _invertSelectionMode = !isPositive;
                System.Diagnostics.Debug.WriteLine($"[AI MODE] First point. Invert mode = {_invertSelectionMode}");
            }
            
            // CRITICAL: Always send POSITIVE points to SAM2 (so it selects something)
            // The Shift key only affects how we interpret the FINAL result, not SAM2 input
            _interactivePoints.Add((physicalX, physicalY, true)); // Always true for SAM2

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
            // Find owner window (FloatingImageWindow)
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this);
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
             Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 var dialogVm = new GothicDialogViewModel { 
                     Title = "Download in Progress", 
                     Message = LocalizationService.Instance["ComponentDownloadingProgress"] ?? "Downloading component..." 
                 };
                 var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };
                 var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                 var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this) as Avalonia.Controls.Window;
                 if (owner != null) dialog.ShowDialog<bool>(owner);
            });
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

    private async Task RemoveBackgroundAsync()
    {
        if (Image == null) return;
        
        // Check if AI is enabled
        if (!_appSettingsService.Settings.EnableAI)
        {
             Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 var dialogVm = new GothicDialogViewModel { Title = "AI Disabled", Message = "AI features are currently disabled in Settings." };
                 var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };
                 
                 var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                 var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this) as Avalonia.Controls.Window;
                 
                 // Fallback: If owner specific to this VM not found, try any active FloatingImageWindow
                 if (owner == null)
                 {
                     owner = desktop?.Windows.OfType<GimmeCapture.Views.Floating.FloatingImageWindow>().FirstOrDefault(w => w.IsActive);
                 }
                 
                 // Final Fallback: Try Main Window or any active window
                 if (owner == null)
                 {
                     owner = desktop?.Windows.FirstOrDefault(w => w.IsActive) ?? desktop?.MainWindow;
                 }
                 
                 if (owner != null) 
                 {
                     dialog.ShowDialog<bool>(owner);
                 }
                 else
                 {
                     // Absolute fallback if no window found (should differ happen)
                     System.Diagnostics.Debug.WriteLine("[Error] No window found to show AI Disabled dialog");
                 }
            });
            return;
        }
        
        // Check Resources and download if needed
        if (!await EnsureAIResourcesAsync()) return;

        try
        {
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["ProcessingAI"] ?? "Processing...";
            
            // Save state for Undo
            PushUndoState();

             // 1. Convert Avalonia Bitmap to Bytes
            byte[] imageBytes;
            using (var ms = new System.IO.MemoryStream())
            {
                // We need to save the current bitmap to stream
                Image.Save(ms);
                imageBytes = ms.ToArray();
            }

            // 2. Process
            using var aiService = new BackgroundRemovalService(_aiResourceService);
            
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
            using var tms = new System.IO.MemoryStream(transparentBytes);
            // Replace the current image with the new transparent one
            var newBitmap = new Bitmap(tms);
            
            // Dispose old image if possible/safe? 
            // Avalonia bitmaps are ref counted roughly, but explicit dispose is good practice if we own it.
            // But we bound it to UI. UI will release ref when binding updates.
            Image = newBitmap; 
            
            // Clear selection after processing
            IsSelectionMode = false;
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AI Processing Failed: {ex}");
            // Show error dialog
             Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 var dialogVm = new GothicDialogViewModel { Title = "Error", Message = ex.Message };
                 var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };
                 var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                 var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this) as Avalonia.Controls.Window;
                 if (owner != null) dialog.ShowDialog<bool>(owner);
            });
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

            // 1. Process with SkiaSharp in a background thread to prevent UI freeze
            var imageBytes = await Task.Run(() =>
            {
                using var originalMs = new System.IO.MemoryStream();
                Image.Save(originalMs);
                using var originalBmp = SKBitmap.Decode(originalMs.ToArray());

                // Use CLEAN mask without crosshairs!
                if (_cleanMaskBytes == null) return null!; // Return empty if no clean mask
                using var maskBmp = SKBitmap.Decode(_cleanMaskBytes);
                
                // RESIZE MASK TO MATCH ORIGINAL BITMAP EXACTLY with Nearest sampling to avoid blurring edges
                // This ensures pixel-perfect alignment with the physical image
                using var resizedMask = maskBmp.Resize(new SKImageInfo(originalBmp.Width, originalBmp.Height), new SKSamplingOptions(SKFilterMode.Nearest));

                using var resultBmp = new SKBitmap(originalBmp.Width, originalBmp.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                
                for (int y = 0; y < originalBmp.Height; y++)
                {
                    for (int x = 0; x < originalBmp.Width; x++)
                    {
                        var color = originalBmp.GetPixel(x, y);
                        var maskColor = resizedMask.GetPixel(x, y);
                        
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
                        
                        resultBmp.SetPixel(x, y, new SKColor(color.Red, color.Green, color.Blue, alpha));
                    }
                }

                using var image = SKImage.FromBitmap(resultBmp);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                return data.ToArray();
            });

            using var resultMs = new System.IO.MemoryStream(imageBytes);
            Image = new Bitmap(resultMs);

            IsPointRemovalMode = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to confirm interactive: {ex}");
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
        ProcessingText = LocalizationService.Instance["StatusInitializingAI"];
        IsProcessing = true;
        try
        {
            await _sam2Service.InitializeAsync();
             // Optimization: Pass current image to AI immediately after initialization
            var skImage = ImageToSkia(Image);
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
        finally
        {
            IsProcessing = false;
        }
        return _sam2Service;
    }

    private SKBitmap? ImageToSkia(Bitmap? avaloniaBitmap)
    {
        if (avaloniaBitmap == null) return null;
        try 
        {
            using var ms = new System.IO.MemoryStream();
            avaloniaBitmap.Save(ms);
            ms.Seek(0, System.IO.SeekOrigin.Begin);
            return SKBitmap.Decode(ms);
        }
        catch { return null; }
    }
}
