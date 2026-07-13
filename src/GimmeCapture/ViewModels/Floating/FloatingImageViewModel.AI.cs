using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Collections.Generic;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System;
using System.Threading.Tasks;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingImageViewModel
{
    private readonly CompositeDisposable _lifecycleDisposables = new();

    private enum InteractiveRemovalState
    {
        Idle,
        Collecting,
        ReadyToConfirm,
        Applying
    }

    private enum InteractiveRemovalMode
    {
        RemoveSelected,
        KeepSelected
    }

    private sealed class InteractiveRemovalSession
    {
        private readonly List<(double X, double Y, bool IsPositive)> _points = new();

        public InteractiveRemovalState State { get; private set; } = InteractiveRemovalState.Idle;
        public InteractiveRemovalMode Mode { get; private set; } = InteractiveRemovalMode.RemoveSelected;
        public SkiaSharp.SKBitmap? CleanMaskBitmap { get; private set; }
        public IReadOnlyList<(double X, double Y, bool IsPositive)> Points => _points;
        public int PointCount => _points.Count;
        public bool IsActive => State != InteractiveRemovalState.Idle;
        public bool CanClick => State == InteractiveRemovalState.Collecting || State == InteractiveRemovalState.ReadyToConfirm;
        public bool CanUndo => PointCount > 0 && CanClick;
        public bool CanConfirm => State == InteractiveRemovalState.ReadyToConfirm && CleanMaskBitmap != null;
        public bool IsKeepSelectedMode => Mode == InteractiveRemovalMode.KeepSelected;

        public void Start()
        {
            State = InteractiveRemovalState.Collecting;
            ResetPoints();
        }

        public void Cancel()
        {
            _points.Clear();
            Mode = InteractiveRemovalMode.RemoveSelected;
            DisposeCleanMask();
            State = InteractiveRemovalState.Idle;
        }

        public void ResetPoints()
        {
            _points.Clear();
            Mode = InteractiveRemovalMode.RemoveSelected;
            DisposeCleanMask();
            if (State != InteractiveRemovalState.Idle)
                State = InteractiveRemovalState.Collecting;
        }

        public bool UndoLastPoint()
        {
            if (!CanUndo)
                return false;

            _points.RemoveAt(_points.Count - 1);
            DisposeCleanMask();
            State = InteractiveRemovalState.Collecting;
            return true;
        }

        public void AddPoint(double x, double y, bool isPositiveInput)
        {
            if (!CanClick)
                return;

            var sam2Positive = isPositiveInput;
            if (_points.Count == 0)
            {
                Mode = isPositiveInput ? InteractiveRemovalMode.RemoveSelected : InteractiveRemovalMode.KeepSelected;
                sam2Positive = true; // First point always defines subject for SAM2.
            }

            _points.Add((x, y, sam2Positive));
            DisposeCleanMask();
            State = InteractiveRemovalState.Collecting;
        }

        public void SetCleanMaskBitmap(SkiaSharp.SKBitmap bitmap)
        {
            DisposeCleanMask();
            CleanMaskBitmap = bitmap;
            if (PointCount > 0 && CleanMaskBitmap != null && State != InteractiveRemovalState.Idle)
                State = InteractiveRemovalState.ReadyToConfirm;
        }

        public bool BeginApplying()
        {
            if (!CanConfirm)
                return false;

            State = InteractiveRemovalState.Applying;
            return true;
        }

        public void EndApplying(bool succeeded)
        {
            if (State != InteractiveRemovalState.Applying)
                return;

            if (succeeded)
            {
                Cancel();
                return;
            }

            State = CanConfirm ? InteractiveRemovalState.ReadyToConfirm : InteractiveRemovalState.Collecting;
        }

        private void DisposeCleanMask()
        {
            CleanMaskBitmap?.Dispose();
            CleanMaskBitmap = null;
        }
    }

    private void InitializeAICommands()
    {
        // _canRemoveBackground is initialized in main constructor
    }

    private void RegisterDisposable(IDisposable disposable)
    {
        _lifecycleDisposables.Add(disposable);
    }

    private ReactiveCommand<Unit, Unit> CreateCommand(Action execute, IObservable<bool>? canExecute, string commandName)
    {
        return ReactiveCommandLifecycleHelper.CreateCommand(
            execute,
            canExecute,
            commandName,
            _lifecycleDisposables,
            nameof(FloatingImageViewModel));
    }

    private ReactiveCommand<Unit, Unit> CreateAsyncCommand(Func<Task> execute, IObservable<bool>? canExecute, string commandName)
    {
        return ReactiveCommandLifecycleHelper.CreateAsyncCommand(
            execute,
            canExecute,
            commandName,
            _lifecycleDisposables,
            nameof(FloatingImageViewModel));
    }

    private ReactiveCommand<TParam, Unit> CreateCommand<TParam>(
        Action<TParam> execute,
        IObservable<bool>? canExecute,
        string commandName)
    {
        return ReactiveCommandLifecycleHelper.CreateCommand(
            execute,
            canExecute,
            commandName,
            _lifecycleDisposables,
            nameof(FloatingImageViewModel));
    }

    private ReactiveCommand<TParam, Unit> CreateAsyncCommand<TParam>(
        Func<TParam, Task> execute,
        IObservable<bool>? canExecute,
        string commandName)
    {
        return ReactiveCommandLifecycleHelper.CreateAsyncCommand(
            execute,
            canExecute,
            commandName,
            _lifecycleDisposables,
            nameof(FloatingImageViewModel));
    }

    public bool CanInteractiveClick => _interactiveSession.CanClick;
    public bool CanUndoInteractivePoint => _interactiveSession.CanUndo;
    public bool CanConfirmInteractive => _interactiveSession.CanConfirm;
    private bool? _lastIsInteractiveSelectionMode;
    private bool? _lastCanInteractiveClick;
    private bool? _lastCanUndoInteractivePoint;
    private bool? _lastCanConfirmInteractive;

    private void StartInteractiveSession()
    {
        _interactiveSession.Start();
        InteractiveMask = null;
        RaiseInteractiveStateChanged();
    }

    private void CancelInteractiveSession()
    {
        _interactiveSession.Cancel();
        InteractiveMask = null;
        RaiseInteractiveStateChanged();
    }

    private void RaiseInteractiveStateChanged()
    {
        RaiseInteractiveBooleanIfChanged(nameof(IsInteractiveSelectionMode), IsInteractiveSelectionMode, ref _lastIsInteractiveSelectionMode);
        RaiseInteractiveBooleanIfChanged(nameof(CanInteractiveClick), CanInteractiveClick, ref _lastCanInteractiveClick);
        RaiseInteractiveBooleanIfChanged(nameof(CanUndoInteractivePoint), CanUndoInteractivePoint, ref _lastCanUndoInteractivePoint);
        RaiseInteractiveBooleanIfChanged(nameof(CanConfirmInteractive), CanConfirmInteractive, ref _lastCanConfirmInteractive);
    }

    private void RaiseInteractiveBooleanIfChanged(string propertyName, bool currentValue, ref bool? lastValue)
    {
        if (lastValue.HasValue && lastValue.Value == currentValue)
            return;

        lastValue = currentValue;
        this.RaisePropertyChanged(propertyName);
    }

    private async Task StartInteractiveRemovalAsync()
    {
        if (CurrentTool != FloatingTool.PointRemoval) 
        {
             return;
        }

        // Check if AI is enabled
        if (!_appSettingsService.Settings.EnableAI)
        {
            DiagnosticText = LocalizationService.Instance["AIDisabled"];
            CurrentTool = FloatingTool.None;

            ShowGothicDialog("AIDisabledTitle", "AIDisabledMessage");
            return;
        }

        if (!await EnsureAIResourcesAsync())
        {
            IsPointRemovalMode = false;
            return;
        }

        EnsureSam2Lease();

        
        IsProcessing = true;
        ProcessingText = LocalizationService.Instance["StatusInitializingAI"];
        
        var sam2 = await GetSAM2ServiceAsync(prepareCurrentImage: false);
        if (sam2 == null) 
        {
            IsProcessing = false;
            IsPointRemovalMode = false;
            if (string.IsNullOrWhiteSpace(DiagnosticText))
            {
                DiagnosticText = "AI init failed.";
            }
            return;
        }

        try
        {
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["StatusInitializingAI"];
            
            StartInteractiveSession();
            Task.Run(async () =>
            {
                try
                {
                    await PrepareCurrentImageForSam2Async(sam2);
                }
                catch (Exception ex)
                {
                    AppLog.Warning("FloatingImage.PrepareSam2", ex);
                }
            }).Forget("FloatingImage.PrepareSam2Background");
            
            DiagnosticText = $"{LocalizationService.Instance["StatusReady"]} [{sam2.ModelVariantName}]";
        }
        catch (Exception ex)
        {
            AppLog.Warning("FloatingImage.StartInteractiveRemoval", ex);
            DiagnosticText = LocalizationService.Instance["StatusError"]; // Or specify a new one
            CurrentTool = FloatingTool.None;

            ShowGothicDialog("AIInitErrorTitle", "AIInitErrorMessage", ex.Message);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public void ResetInteractivePoints()
    {
        _interactiveSession.ResetPoints();
        InteractiveMask = null;
        DiagnosticText = "AI: Points Reset";
        System.Diagnostics.Debug.WriteLine("FloatingVM: Resetting interactive points");
        RaiseInteractiveStateChanged();
    }

    public async Task UndoLastPointAsync()
    {
        if (_interactiveSession.UndoLastPoint())
        {
            // CRITICAL: Reset the AI's mask feedback memory when undoing.
            // If the last result was a "bad" full-image mask, we don't want the AI to reuse it.
            if (_interactiveSession.PointCount == 0)
            {
                ResetInteractivePoints();
            }
            else
            {
                await RefineMaskAsync();
            }

            RaiseInteractiveStateChanged();
        }
    }

    // Synchronous wrapper for right-click undo
    public void UndoLastInteractivePoint()
    {
        UndoLastPointAsync().Forget("FloatingImage.UndoLastPoint");
    }

    private async Task RefineMaskAsync()
    {
        // Check Resources and download if needed (SAM2)
        if (!await EnsureAIResourcesAsync()) return;

        var sam2 = await GetSAM2ServiceAsync();
        if (sam2 == null) return;
        if (!await PrepareCurrentImageForSam2Async(sam2)) return;
    
        DiagnosticText = "AI: Refining...";
        try
        {
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["StatusProcessing"];
            
            var cleanMask = await sam2.GetMaskBitmapAsync(_interactiveSession.Points);
            var iouInfo = sam2.LastIouInfo;
            DiagnosticText = $"AI: ({_interactiveSession.PointCount} pts) {iouInfo}";

            if (cleanMask != null)
            {
                _interactiveSession.SetCleanMaskBitmap(cleanMask);

                SkiaSharp.SKColor overlayColor = _interactiveSession.IsKeepSelectedMode
                    ? new SkiaSharp.SKColor(0, 255, 100, 150)   // Green for "Keep mode" (Shift+Click)
                    : new SkiaSharp.SKColor(255, 80, 80, 150);  // Red for "Remove mode" (Normal)

                using var coloredMask = new SkiaSharp.SKBitmap(cleanMask.Width, cleanMask.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
                unsafe
                {
                    byte* grayBase = (byte*)cleanMask.GetPixels().ToPointer();
                    uint* colorBase = (uint*)coloredMask.GetPixels().ToPointer();
                    uint overlayRaw = (uint)overlayColor.Blue
                        | ((uint)overlayColor.Green << 8)
                        | ((uint)overlayColor.Red << 16)
                        | ((uint)overlayColor.Alpha << 24);

                    for (int y = 0; y < cleanMask.Height; y++)
                    {
                        byte* grayRow = grayBase + (y * cleanMask.RowBytes);
                        uint* colorRow = (uint*)((byte*)colorBase + (y * coloredMask.RowBytes));
                        for (int x = 0; x < cleanMask.Width; x++)
                        {
                            colorRow[x] = grayRow[x] > 127 ? overlayRaw : 0u;
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

                    foreach (var pt in _interactiveSession.Points)
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

                if (!FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(coloredMask, out var interactiveMaskBitmap, out var interactiveMaskError))
                    throw new Exception(interactiveMaskError ?? "Failed to create interactive mask preview.");
                InteractiveMask = interactiveMaskBitmap;
                RaiseInteractiveStateChanged();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingImage.RefineMask", ex);
            DiagnosticText = $"Refine Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public async Task HandlePointClickAsync(double x, double y, bool isPositive = true)
    {
        if (IsProcessing || !CanInteractiveClick) return;
        
        // LOG PHYSICAL PIXEL COORDINATES FOR USER VERIFICATION
        System.Diagnostics.Debug.WriteLine($"[AI DEBUG] Click Pixel: ({x:F0}, {y:F0}) Type: {(isPositive ? "Positive" : "Negative")}");
        
        var physicalX = x;
        var physicalY = y;

        if (_sam2Service == null || !_interactiveSession.IsActive) return;

        try
        {
            var wasFirstPoint = _interactiveSession.PointCount == 0;
            _interactiveSession.AddPoint(physicalX, physicalY, isPositive);
            if (_interactiveSession.PointCount == 1)
            {
                System.Diagnostics.Debug.WriteLine($"[AI MODE] First point. Keep selected mode = {_interactiveSession.IsKeepSelectedMode}");
            }
            if (wasFirstPoint)
                RaiseInteractiveStateChanged();

            await RefineMaskAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingImage.HandlePointClick", ex);
            DiagnosticText = $"Click Error: {ex.Message}";
        }
    }

    private async Task<bool> ShowDownloadConfirmationAsync()
    {
        var msg = LocalizationService.Instance["AIDownloadConfirm"] ?? "Interactive AI Selection requires additional modules. Download now?";
        bool confirmed = false;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (ConfirmDialogAction != null)
            {
                confirmed = await ConfirmDialogAction(msg);
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
        var currentStatus = _resourceQueue.GetSnapshot("AI")?.Status;
        if (currentStatus is ResourceQueueStatus.Pending or ResourceQueueStatus.Running)
        {
            ShowGothicDialog("StatusProcessing", "ComponentDownloadingProgress");
            return false;
        }

        // 3. Not ready, Not downloading -> Ask for permission
        var confirmed = await ShowDownloadConfirmationAsync();
        if (!confirmed) return false;

        // 4. Start Download (Fire and Forget from UI perspective)
        _resourceQueue.EnqueueAsync("AI", async ct =>
        {
             // Download Core and Selected Variant
             bool coreReady = await _aiResourceService.EnsureAICoreAsync(ct);
             if (!coreReady) return false;
             
             var variant = _appSettingsService.Settings.SelectedSAM2Variant;
             return await _aiResourceService.EnsureSAM2Async(variant, ct);
        }).Forget("FloatingImage.EnqueueAI");

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
        var currentStatus = _resourceQueue.GetSnapshot("AI Core")?.Status;
        if (currentStatus is ResourceQueueStatus.Pending or ResourceQueueStatus.Running)
        {
            ShowGothicDialog("StatusProcessing", "ComponentDownloadingProgress");
            return false;
        }

        // 3. Not ready, Not downloading -> Ask for permission
        var msg = LocalizationService.Instance["AIDownloadConfirm"] ?? "Background Removal requires additional modules. Download now?";
        bool confirmed = false;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (ConfirmDialogAction != null)
            {
                confirmed = await ConfirmDialogAction(msg);
            }
        });
        
        if (!confirmed) return false;

        // 4. Start Download
        _resourceQueue.EnqueueAsync("AI Core", async ct =>
        {
             return await _aiResourceService.EnsureAICoreAsync(ct);
        }).Forget("FloatingImage.EnqueueAICore");

        return false;
    }

    private async Task RemoveBackgroundAsync()
    {
        if (Image == null) return;
        
        // Check if AI is enabled
        if (!_appSettingsService.Settings.EnableAI)
        {
            ShowGothicDialog("AIDisabledTitle", "AIDisabledMessage");
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
            AppLog.Error("FloatingImage.RemoveBackground", ex);
            ShowErrorDialog(ex.Message);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task ConfirmInteractiveAsync()
    {
        if (Image == null || InteractiveMask == null || !_interactiveSession.BeginApplying()) return;

        var applySucceeded = false;
        try
        {
            RaiseInteractiveStateChanged();
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["ProcessingAI"] ?? "Applying Removal...";
            
            PushUndoState();

            var sourceImage = Image;
            var cleanMaskBitmap = _interactiveSession.CleanMaskBitmap;
            if (sourceImage == null) throw new Exception("Source image is unavailable.");
            if (cleanMaskBitmap == null) throw new Exception("No valid mask generated.");

            // 1. Process with SkiaSharp in a background thread to prevent UI freeze
            var confirmedBitmap = await Task.Run(() =>
            {
                if (!FloatingBitmapConversionHelper.TryCopyToSkBitmap(sourceImage, out var originalBmp, out var copyError) || originalBmp == null)
                    throw new Exception(copyError ?? "Failed to prepare source image.");

                using (originalBmp)
                {
                    using var resultBmp = new SkiaSharp.SKBitmap(originalBmp.Width, originalBmp.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
                    var maskScaleX = (double)cleanMaskBitmap.Width / originalBmp.Width;
                    var maskScaleY = (double)cleanMaskBitmap.Height / originalBmp.Height;

                    unsafe
                    {
                        uint* srcBase = (uint*)originalBmp.GetPixels().ToPointer();
                        uint* dstBase = (uint*)resultBmp.GetPixels().ToPointer();
                        byte* maskBase = (byte*)cleanMaskBitmap.GetPixels().ToPointer();

                        for (int y = 0; y < originalBmp.Height; y++)
                        {
                            uint* srcRow = (uint*)((byte*)srcBase + (y * originalBmp.RowBytes));
                            uint* dstRow = (uint*)((byte*)dstBase + (y * resultBmp.RowBytes));
                            int maskY = Math.Clamp((int)(y * maskScaleY), 0, cleanMaskBitmap.Height - 1);
                            byte* maskRow = maskBase + (maskY * cleanMaskBitmap.RowBytes);

                            for (int x = 0; x < originalBmp.Width; x++)
                            {
                                uint src = srcRow[x];
                                int maskX = Math.Clamp((int)(x * maskScaleX), 0, cleanMaskBitmap.Width - 1);
                                bool isSelected = maskRow[maskX] > 127;
                                if (_interactiveSession.IsKeepSelectedMode)
                                {
                                    isSelected = !isSelected;
                                }

                                byte alpha = isSelected ? byte.MinValue : (byte)(src >> 24);
                                dstRow[x] = (src & 0x00FFFFFFu) | ((uint)alpha << 24);
                            }
                        }
                    }

                    if (!FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(resultBmp, out var detachedBitmap, out var detachError) || detachedBitmap == null)
                        throw new Exception(detachError ?? "Failed to materialize interactive output.");

                    return detachedBitmap;
                }
            });

            Image = confirmedBitmap;
            applySucceeded = true;

            IsPointRemovalMode = false;
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingImage.ConfirmInteractive", ex);
            ShowErrorDialog($"Failed to apply background removal: {ex.Message}");
        }
        finally
        {
            _interactiveSession.EndApplying(applySucceeded);
            RaiseInteractiveStateChanged();
            IsProcessing = false;
        }
    }

    private async Task<SAM2Service?> GetSAM2ServiceAsync(bool prepareCurrentImage = false)
    {
        if (_sam2Service == null)
        {
            _sam2Service = new SAM2Service(_sam2RuntimeService, _appSettingsService);
        }

        try
        {
            await _sam2Service.InitializeAsync();

            if (prepareCurrentImage && !await PrepareCurrentImageForSam2Async(_sam2Service))
            {
                DiagnosticText = "AI prepare failed.";
                return null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingImage.GetSAM2Service", ex);
            DiagnosticText = $"AI init failed: {ex.Message}";
            _sam2Service = null;
        }
        return _sam2Service;
    }

    private async Task<bool> PrepareCurrentImageForSam2Async(SAM2Service sam2)
    {
        using var skImage = FloatingBitmapConversionHelper.ToSkBitmap(Image);
        if (skImage == null)
        {
            DiagnosticText = "AI prepare failed: image conversion failed.";
            return false;
        }

        try
        {
            await sam2.EnsureImagePreparedAsync(skImage);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingImage.PrepareImageForSam2", ex);
            DiagnosticText = $"AI prepare failed: {ex.Message}";
            return false;
        }
    }

    private void ShowErrorDialog(string message)
    {
        ShowGothicDialog("StatusError", message);
    }

    private void ShowGothicDialog(string titleKey, string messageKeyOrText, params object[] formatArgs)
    {
        var title = LocalizeKeyOrText(titleKey);
        var messageTemplate = LocalizeKeyOrText(messageKeyOrText);
        var message = formatArgs.Length > 0
            ? string.Format(messageTemplate, formatArgs)
            : messageTemplate;

        ShowGothicDialogOnUiThread(title, message);
    }

    private string LocalizeKeyOrText(string keyOrText)
    {
        if (string.IsNullOrWhiteSpace(keyOrText))
            return string.Empty;

        var localized = LocalizationService.Instance[keyOrText];
        return string.IsNullOrWhiteSpace(localized) ? keyOrText : localized;
    }

    private void ShowGothicDialogOnUiThread(string title, string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ShowDialogAction == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Dialog] Owner not found. Title={title}, Message={message}");
                return;
            }

            ShowDialogAction(title, message);
        });
    }
}
