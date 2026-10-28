using Avalonia.Media.Imaging;
using ReactiveUI;
using ReactiveUI.Reactive;
using System.Reactive;
using System.Collections.Generic;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using System.Reactive.Linq;
using System.Reactive.Disposables;
using System;
using System.Threading;
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
            DiagnosticText = Localization["AIDisabled"];
            CurrentTool = FloatingTool.None;

            ShowMessageDialog("AIDisabledTitle", "AIDisabledMessage");
            return;
        }

        if (!await EnsureAIResourcesAsync())
        {
            IsPointRemovalMode = false;
            return;
        }

        EnsureSam2Lease();

        
        IsProcessing = true;
        ProcessingText = Localization["StatusInitializingAI"];
        
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
            ProcessingText = Localization["StatusInitializingAI"];
            
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
            
            DiagnosticText = $"{Localization["StatusReady"]} [{sam2.ModelVariantName}]";
        }
        catch (Exception ex)
        {
            AppLog.Warning("FloatingImage.StartInteractiveRemoval", ex);
            DiagnosticText = Localization["StatusError"]; // Or specify a new one
            CurrentTool = FloatingTool.None;

            ShowMessageDialog("AIInitErrorTitle", "AIInitErrorMessage", ex.Message);
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
        AppLog.Debug("FloatingImageViewModel.AI", "FloatingVM: Resetting interactive points");
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
            ProcessingText = Localization["StatusProcessing"];
            
            var cleanMask = await sam2.GetMaskBitmapAsync(_interactiveSession.Points);
            var iouInfo = sam2.LastIouInfo;
            DiagnosticText = $"AI: ({_interactiveSession.PointCount} pts) {iouInfo}";

            if (cleanMask != null)
            {
                SkiaSharp.SKColor overlayColor = _interactiveSession.IsKeepSelectedMode
                    ? new SkiaSharp.SKColor(0, 255, 100, 150)   // Green for "Keep mode" (Shift+Click)
                    : new SkiaSharp.SKColor(255, 80, 80, 150);  // Red for "Remove mode" (Normal)
                (double X, double Y, bool IsPositive)[] points = [.. _interactiveSession.Points];
                int imageWidth = Image?.PixelSize.Width ?? 1;
                int imageHeight = Image?.PixelSize.Height ?? 1;

                // The preview is drawn and handed to Avalonia off the UI thread, which spent about 18 ms of every
                // click on it at 3840x2400. It works from copies of what the UI thread can change meanwhile, and the
                // session gets the mask only after, since the session disposes it when the next point comes in.
                Bitmap interactiveMaskBitmap;
                try
                {
                    interactiveMaskBitmap = await Task.Run(() =>
                        CreateInteractiveMaskPreview(cleanMask, overlayColor, points, imageWidth, imageHeight));
                }
                finally
                {
                    _interactiveSession.SetCleanMaskBitmap(cleanMask);
                }

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

    /// <summary>
    /// A click's preview as Avalonia shows it: <paramref name="mask"/> coloured in, with a marker at each of
    /// <paramref name="points"/>. Only pixel work, so it runs off the UI thread.
    /// </summary>
    private static Bitmap CreateInteractiveMaskPreview(
        SkiaSharp.SKBitmap mask,
        SkiaSharp.SKColor overlayColor,
        IReadOnlyList<(double X, double Y, bool IsPositive)> points,
        int imageWidth,
        int imageHeight)
    {
        using var coloredMask = InteractiveMaskPixels.CreateOverlay(mask, overlayColor);
        DrawPointMarkers(coloredMask, points, imageWidth, imageHeight);

        if (!FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(coloredMask, out var preview, out var error) || preview == null)
            throw new Exception(error ?? "Failed to create interactive mask preview.");
        return preview;
    }

    /// <summary>
    /// Marks each of <paramref name="points"/> on <paramref name="preview"/>. The points are in the pin's pixels
    /// (<paramref name="imageWidth"/> by <paramref name="imageHeight"/>) and scaled to the preview's. Internal for
    /// <c>FloatingImageSmartSelectionTests</c>.
    /// </summary>
    internal static void DrawPointMarkers(
        SkiaSharp.SKBitmap preview,
        IReadOnlyList<(double X, double Y, bool IsPositive)> points,
        int imageWidth,
        int imageHeight)
    {
        using var canvas = new SkiaSharp.SKCanvas(preview);
        using var posPaint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.LimeGreen, Style = SkiaSharp.SKPaintStyle.Fill, IsAntialias = true };
        using var negPaint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Red, Style = SkiaSharp.SKPaintStyle.Fill, IsAntialias = true };

        // Scale points to match the mask bitmap size
        float scaleX = (float)preview.Width / imageWidth;
        float scaleY = (float)preview.Height / imageHeight;

        foreach (var pt in points)
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

    public async Task HandlePointClickAsync(double x, double y, bool isPositive = true)
    {
        if (IsProcessing || !CanInteractiveClick) return;
        
        // LOG PHYSICAL PIXEL COORDINATES FOR USER VERIFICATION
        AppLog.Debug("FloatingImageViewModel.AI", $"[AI DEBUG] Click Pixel: ({x:F0}, {y:F0}) Type: {(isPositive ? "Positive" : "Negative")}");
        
        var physicalX = x;
        var physicalY = y;

        if (_sam2Service == null || !_interactiveSession.IsActive) return;

        try
        {
            var wasFirstPoint = _interactiveSession.PointCount == 0;
            _interactiveSession.AddPoint(physicalX, physicalY, isPositive);
            if (_interactiveSession.PointCount == 1)
            {
                AppLog.Debug("FloatingImageViewModel.AI", $"[AI MODE] First point. Keep selected mode = {_interactiveSession.IsKeepSelectedMode}");
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
        var title = Localization["AIDownloadTitle"];
        var msg = Localization["AIDownloadConfirm"];
        bool confirmed = false;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (ConfirmDialogAction != null)
            {
                confirmed = await ConfirmDialogAction(title, msg);
            }
        });
        
        return confirmed;
    }

    public async Task<bool> EnsureAIResourcesAsync()
    {
        // 1. Check if already ready - Fast path. SAM2 alone: AI Core used to be needed for the ONNX Runtime it
        // downloaded, and the runtime ships with the app now, so AI Core is only background removal's model.
        var variant = _appSettingsService.Settings.SelectedSAM2Variant;
        if (_aiResourceService.IsSAM2Ready(variant)) return true;

        // 2. Check if already downloading (Background)
        var currentStatus = _resourceQueue.GetSnapshot("AI")?.Status;
        if (currentStatus is ResourceQueueStatus.Pending or ResourceQueueStatus.Running)
        {
            ShowMessageDialog("StatusProcessing", "ComponentDownloadingProgress");
            return false;
        }

        // 3. Not ready, Not downloading -> Ask for permission
        var confirmed = await ShowDownloadConfirmationAsync();
        if (!confirmed) return false;

        // 4. Start Download (Fire and Forget from UI perspective)
        _resourceQueue.EnqueueAsync("AI", async ct =>
        {
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
            ShowMessageDialog("StatusProcessing", "ComponentDownloadingProgress");
            return false;
        }

        // 3. Not ready, Not downloading -> Ask for permission
        // Background removal's own question (the ~170 MB U2Net model); it used to ask interactive selection's
        // (~100 MB).
        var title = Localization["AIDownloadTitle"];
        var msg = Localization["AIDownloadPrompt"];
        bool confirmed = false;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (ConfirmDialogAction != null)
            {
                confirmed = await ConfirmDialogAction(title, msg);
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
            ShowMessageDialog("AIDisabledTitle", "AIDisabledMessage");
            return;
        }
        
        // Check Resources and download if needed (Core only, not SAM2)
        if (!await EnsureAICoreResourcesAsync()) return;

        try
        {
            IsProcessing = true;
            ProcessingText = Localization["ProcessingAI"] ?? "Processing...";
            
            // Save state for Undo
            PushUndoState();

            // 1. Copy the Avalonia Bitmap into an SKBitmap, off the UI thread. No PNG in between: the removal
            // service works on pixels, and encoding one for a 4K pin took over 2 s on its own. Unpremultiplied: the
            // service keeps the colour of each pixel it keeps and hands it back as unpremultiplied, so premultiplied
            // colour was premultiplied again on the way back, darkening the soft edges an earlier removal left.
            var source = Image;
            if (source == null) return;
            var (copied, sourcePixels, copyError) = await Task.Run(() =>
                (FloatingBitmapConversionHelper.TryCopyToSkBitmap(source, out var bitmap, out var error, SkiaSharp.SKAlphaType.Unpremul), bitmap, error));
            if (!copied || sourcePixels == null)
                throw new Exception(copyError ?? "Failed to read the image pixels.");
            using var sourceBitmap = sourcePixels;

            // 2. Process — reuse this pin's background-removal service (created once, kept warm ~10s between
            // removals by its own idle scheduler) instead of new'ing/disposing a fresh U2Net session each time.
            // Recreate if a global AI unload (module removal) disposed the shared instance, so the rebuilt
            // session stays subscribed to future global-unload requests.
            if (_backgroundRemoval == null || _backgroundRemoval.IsDisposed)
            {
                _backgroundRemoval = new BackgroundRemovalService(_aiResourceService, _pathService);
            }
            var aiService = _backgroundRemoval;

            // SelectionRect is in logical pixels (UI space). 
            // We need to scale it to physical image pixels for BackgroundRemovalService.
            Avalonia.Rect? scaledRect = null;
            if (IsSelectionActive)
            {
                // Must use current DisplayWidth/Height for scaling the UI selection to physical pixels
                var refW = DisplayWidth > 0 ? DisplayWidth : OriginalWidth;
                var refH = DisplayHeight > 0 ? DisplayHeight : OriginalHeight;
                var scaleX = (double)source.PixelSize.Width / refW;
                var scaleY = (double)source.PixelSize.Height / refH;
                scaledRect = new Avalonia.Rect(
                    SelectionRect.X * scaleX,
                    SelectionRect.Y * scaleY,
                    SelectionRect.Width * scaleX,
                    SelectionRect.Height * scaleY);
            }

            using var processed = await aiService.RemoveBackgroundAsync(sourceBitmap, scaledRect);

            // 3. Hand the processed pixels to Avalonia, off the UI thread too.
            var (converted, detachedBitmap, convertError) = await Task.Run(() =>
                (FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(processed, out var bitmap, out var error), bitmap, error));
            if (!converted)
                throw new Exception(convertError ?? "Failed to read the processed image.");
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
            ProcessingText = Localization["ProcessingAI"] ?? "Applying Removal...";
            
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
                    using var resultBmp = InteractiveMaskPixels.ApplyToImage(originalBmp, cleanMaskBitmap, _interactiveSession.IsKeepSelectedMode);

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

    private async Task<ISam2Service?> GetSAM2ServiceAsync(bool prepareCurrentImage = false)
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

    // The pin bitmap the SAM2 service last prepared, and which service. Every click used to copy the whole pin on the
    // UI thread (about 10 ms and 37 MB at 3840x2400) only for the service to find its seven sample pixels unchanged.
    private sealed record Sam2PreparedImage(ISam2Service Service, Bitmap Image);
    private Sam2PreparedImage? _sam2PreparedImage;
    // One prepare at a time, so the bitmap remembered is the one the service prepared last.
    private readonly SemaphoreSlim _sam2PrepareGate = new(1, 1);

    private async Task<bool> PrepareCurrentImageForSam2Async(ISam2Service sam2)
    {
        await _sam2PrepareGate.WaitAsync();
        try
        {
            // Nothing to copy while the service holds this very bitmap: HandleImageChangedForSam2 forgets it whenever
            // Image changes, and the service lets go of it when the model is switched.
            var image = Image;
            if (image != null && sam2.IsImagePrepared && _sam2PreparedImage is { } prepared
                && ReferenceEquals(prepared.Service, sam2) && ReferenceEquals(prepared.Image, image))
            {
                return true;
            }

            // Off the UI thread, as background removal's copy is.
            using var skImage = image == null ? null : await Task.Run(() => FloatingBitmapConversionHelper.ToSkBitmap(image));
            if (image == null || skImage == null)
            {
                DiagnosticText = "AI prepare failed: image conversion failed.";
                return false;
            }

            try
            {
                await sam2.EnsureImagePreparedAsync(skImage);
                _sam2PreparedImage = new Sam2PreparedImage(sam2, image);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("FloatingImage.PrepareImageForSam2", ex);
                DiagnosticText = $"AI prepare failed: {ex.Message}";
                return false;
            }
        }
        finally
        {
            _sam2PrepareGate.Release();
        }
    }

    private void ShowErrorDialog(string message)
    {
        ShowMessageDialog("StatusError", message);
    }

    private void ShowMessageDialog(string titleKey, string messageKeyOrText, params object[] formatArgs)
    {
        var title = LocalizeKeyOrText(titleKey);
        var messageTemplate = LocalizeKeyOrText(messageKeyOrText);
        var message = formatArgs.Length > 0
            ? string.Format(messageTemplate, formatArgs)
            : messageTemplate;

        ShowMessageDialogOnUiThread(title, message);
    }

    private string LocalizeKeyOrText(string keyOrText)
    {
        if (string.IsNullOrWhiteSpace(keyOrText))
            return string.Empty;

        var localized = Localization[keyOrText];
        return string.IsNullOrWhiteSpace(localized) ? keyOrText : localized;
    }

    private void ShowMessageDialogOnUiThread(string title, string message)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ShowDialogAction == null)
            {
                AppLog.Debug("FloatingImageViewModel.AI", $"[Dialog] Owner not found. Title={title}, Message={message}");
                return;
            }

            ShowDialogAction(title, message);
        });
    }
}
