using Avalonia;
using GimmeCapture.Models;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.OCR;
using GimmeCapture.Services.Platforms.Windows;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    private Rect _detectedRect;
    public Rect DetectedRect
    {
        get => _detectedRect;
        set => this.RaiseAndSetIfChanged(ref _detectedRect, value);
    }

    public ObservableCollection<VisualRect> WindowRects { get; } = new();
    private readonly WindowDetectionService _detectionService = new();

    public void RefreshWindowRects(IntPtr? excludeHWnd = null)
    {
        // Get global rects (Physical pixels)
        var globalRects = _detectionService.GetVisibleWindowRects(excludeHWnd);
        
        // Translate to local coordinates based on ScreenOffset (Physical)
        // AND convert to logical coordinates by dividing by VisualScaling
        var localRects = globalRects
            .Select(r => new VisualRect(
                (r.X - ScreenOffset.X) / VisualScaling, 
                (r.Y - ScreenOffset.Y) / VisualScaling, 
                r.Width / VisualScaling, 
                r.Height / VisualScaling));
        
        WindowRects.Clear();
        foreach (var rect in localRects)
        {
            WindowRects.Add(rect);
        }
    }

    public void UpdateDetectedRect(Point mousePos)
    {
        if (CurrentState != SnipState.Detecting) return;
        
        // Convert VisualRects back to Rects for detection service (or update detection service)
        // Since VisualRect is simple, we can just project it.
        var rectList = WindowRects.Select(vr => new Rect(vr.X, vr.Y, vr.Width, vr.Height)).ToList();
        var rect = _detectionService.GetRectAtPoint(mousePos, rectList);
        
        DetectedRect = rect ?? new Rect(0,0,0,0);
    }

    private System.Threading.CancellationTokenSource? _scanCts;

    private async Task RunAIScanAsync()
    {
        var engine = _mainVm?.AIScanEngine ?? AIScanEngine.OCR;
        if (engine == AIScanEngine.SAM2)
        {
            await RunSAM2ScanAsync();
            return;
        }

        await RunOCRScanAsync();
    }

    private async Task RunSAM2ScanAsync()
    {
        System.Diagnostics.Debug.WriteLine("[AI Scan] RunAIScanAsync started");

        // Don't run AI detection if we are actually recording (RecState is not Idle)
        // But ALLOW it if we are just in "Recording Mode" (preparing to record)
        if (RecState != RecordingState.Idle) 
        {
            System.Diagnostics.Debug.WriteLine($"[AI Scan] Abort: RecState is {RecState}");
            return;
        }

        if (_mainVm == null || !_mainVm.EnableAI) 
        {
            System.Diagnostics.Debug.WriteLine($"[AI Scan] Abort: EnableAI is false or MainVm is null");
            return;
        }

        // USER REQUEST: This setting controls the SCANNING PROCESS itself.
        // If disabled, we do NOT run the expensive SAM2 detection.
        if (!_mainVm.EnableAIScan)
        {
            System.Diagnostics.Debug.WriteLine("[AI Scan] Abort: EnableAIScan is false");
            return;
        }

        // Cancel previous scan if any
        _scanCts?.Cancel();
        _scanCts = new System.Threading.CancellationTokenSource();
        var token = _scanCts.Token;

        ShowTopLoadingBar = true;
        Console.WriteLine("[AI Scan] ShowTopLoadingBar set to TRUE");
        
        try
        {
            if (CurrentState == SnipState.Detecting) ProcessingText = LocalizationService.Instance["StatusInitializingAI"] ?? "Initializing AI Models...";
            // Check AI resources first
            var aiReady = _mainVm.AIResourceService.IsSAM2Ready(_mainVm.AppSettingsService.Settings.SelectedSAM2Variant);
            Console.WriteLine($"[AI Scan] SAM2 Ready: {aiReady}");
            
            if (!aiReady)
            {
                if (CurrentState == SnipState.Detecting) ProcessingText = LocalizationService.Instance["StatusSAM2NotFound"] ?? "ABORT: SAM2 models not found. Please download in settings.";
                Console.WriteLine("[AI Scan] ABORT: SAM2 not ready - model may not be downloaded");
                await Task.Delay(2000, token);
                ShowTopLoadingBar = false;
                return;
            }

            token.ThrowIfCancellationRequested();

            // 1. Capture full screen for SAM2 encoding
            var originalOpacity = MaskOpacity;
            MaskOpacity = 0;
            await Task.Delay(100, token); // Let mask hide

            var regionToCapture = new Rect(0, 0, ViewportSize.Width, ViewportSize.Height);
            Console.WriteLine($"[AI Scan] Capturing region: {regionToCapture}");
            
            using var skBitmap = await _captureService.CaptureScreenAsync(regionToCapture, ScreenOffset, VisualScaling, false);
            
            MaskOpacity = originalOpacity;
            
            if (skBitmap == null) 
            {
                Console.WriteLine("[AI Scan] ABORT: Capture returned null");
                return;
            }
            
            Console.WriteLine($"[AI Scan] Captured bitmap: {skBitmap.Width}x{skBitmap.Height}");
            
            token.ThrowIfCancellationRequested();

            // 2. Run scan using persistent SAM2 service (Preloaded and Warmed up)
            if (_sam2Service == null) return;
            
            Console.WriteLine("[AI Scan] Using preloaded SAM2 service...");
            await _sam2Service.InitializeAsync(); // Ensures it's ready if preload was slow
            
            Console.WriteLine("[AI Scan] Setting image (Fast path)...");
            if (CurrentState == SnipState.Detecting) ProcessingText = LocalizationService.Instance["StatusAIEncoding"] ?? "AI Encoding Image...";
            await _sam2Service.SetImageAsync(skBitmap);
            Console.WriteLine("[AI Scan] Image set. Running AutoDetect...");
            if (CurrentState == SnipState.Detecting) ProcessingText = LocalizationService.Instance["StatusAIDetecting"] ?? "Detecting Objects...";

            token.ThrowIfCancellationRequested();

            // Use higher grid density for better detection on high-res screens
            int gridDensity = Math.Max(24, _mainVm.SAM2GridDensity);
            var rects = await _sam2Service.AutoDetectObjectsAsync(gridDensity, _mainVm.SAM2MaxObjects, _mainVm.SAM2MinObjectSize, token); 
            // Do NOT dispose persistent service here
            
            Console.WriteLine($"[AI Scan] AutoDetect returned {rects.Count} rects");

            token.ThrowIfCancellationRequested();

            // 4. Add detected rects to WindowRects
            if (rects.Any())
            {
                // Only add to WindowRects (visual red boxes) if the setting is enabled
                if (_mainVm.ShowAIScanBox)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        
                        // Guard: If user has already started selecting or finished, or AI disabled, don't show rects
                        if (CurrentState != SnipState.Detecting || _mainVm?.EnableAI != true) return;

                        int addedCount = 0;
                        double scale = VisualScaling > 0 ? VisualScaling : 1.0;
                        
                        foreach (var r in rects)
                        {
                             if (token.IsCancellationRequested) break;

                            // Filter small objects (e.g. < 50x50 = 2500 area)
                            double logicalWidth = r.Width / scale;
                            double logicalHeight = r.Height / scale;
                            double area = logicalWidth * logicalHeight;
                            double viewportArea = ViewportSize.Width * ViewportSize.Height;
                            
                            // Filter tiny objects AND full-screen objects (> 95% of screen)
                            if (logicalWidth >= 20 && logicalHeight >= 20 && area < (viewportArea * 0.95))
                            {
                                // Convert to logical coordinates for display
                                var logicalRect = new Rect(r.X / scale, r.Y / scale, logicalWidth, logicalHeight);
                                WindowRects.Add(new VisualRect(logicalRect));
                                addedCount++;
                            }
                        }
                        Console.WriteLine($"[AI Scan] Complete: {addedCount} objects added (filtered from {rects.Count})");
                    });
                }
                else
                {
                     Console.WriteLine($"[AI Scan] Complete: {rects.Count} objects detected (Hidden by setting)");
                }
            }
            else
            {
                Console.WriteLine("[AI Scan] No objects detected");
            }
        }
        catch (OperationCanceledException)
        {
             Console.WriteLine("[AI Scan] CANCELLED");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AI Scan] ERROR: {ex.Message}");
            Console.WriteLine($"[AI Scan] Stack: {ex.StackTrace}");
        }
        finally
        {
            ShowTopLoadingBar = false;
            Console.WriteLine("[AI Scan] Finished");
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private async Task RunOCRScanAsync()
    {
        System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] RunOCRScanAsync started");

        if (RecState != RecordingState.Idle)
        {
            System.Diagnostics.Debug.WriteLine($"[AI Scan][OCR] Abort: RecState is {RecState}");
            return;
        }

        if (_mainVm == null || !_mainVm.EnableAI)
        {
            System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] Abort: EnableAI is false or MainVm is null");
            return;
        }

        if (!_mainVm.EnableAIScan)
        {
            System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] Abort: EnableAIScan is false");
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new System.Threading.CancellationTokenSource();
        var token = _scanCts.Token;

        ShowTopLoadingBar = true;

        try
        {
            if (CurrentState == SnipState.Detecting)
                ProcessingText = LocalizationService.Instance["StatusAIScanning"] ?? "AI Scanning...";

            var ocrReady = await _mainVm.AIResourceService.EnsureOCRAsync();
            if (!ocrReady)
            {
                System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] OCR resources are not ready");
                return;
            }

            token.ThrowIfCancellationRequested();

            var regionToCapture = new Rect(0, 0, ViewportSize.Width, ViewportSize.Height);
            using var bitmap = await _captureService.CaptureScreenAsync(regionToCapture, ScreenOffset, VisualScaling, false);
            if (bitmap == null)
            {
                System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] Capture returned null");
                return;
            }

            token.ThrowIfCancellationRequested();

            var ocrEngine = new PaddleOCREngine(_mainVm.AIResourceService, _mainVm.AppSettingsService);
            var ocrLang = _mainVm.AppSettingsService.Settings.SourceLanguage;
            await ocrEngine.EnsureLoadedAsync(ocrLang);
            var textBoxes = await Task.Run(() => ocrEngine.DetectText(bitmap), token);
            ocrEngine.Dispose();

            token.ThrowIfCancellationRequested();

            if (_mainVm.ShowAIScanBox)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    if (CurrentState != SnipState.Detecting || _mainVm?.EnableAI != true) return;

                    WindowRects.Clear();

                    double scaleX = ViewportSize.Width / bitmap.Width;
                    double scaleY = ViewportSize.Height / bitmap.Height;
                    foreach (var box in textBoxes)
                    {
                        var logicalRect = new Rect(
                            box.Left * scaleX,
                            box.Top * scaleY,
                            box.Width * scaleX,
                            box.Height * scaleY);

                        if (logicalRect.Width >= 12 && logicalRect.Height >= 8)
                        {
                            WindowRects.Add(new VisualRect(logicalRect));
                        }
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[AI Scan][OCR] CANCELLED");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AI Scan][OCR] ERROR: {ex.Message}");
        }
        finally
        {
            ShowTopLoadingBar = false;
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    private Rect _activeScreenBounds = new Rect(0,0,1920,1080); // Default
    public Rect ActiveScreenBounds
    {
        get => _activeScreenBounds;
        set => this.RaiseAndSetIfChanged(ref _activeScreenBounds, value);
    }

    private ObservableCollection<ScreenBoundsViewModel> _allScreenBounds = new();
    public ObservableCollection<ScreenBoundsViewModel> AllScreenBounds
    {
        get => _allScreenBounds;
        set => this.RaiseAndSetIfChanged(ref _allScreenBounds, value);
    }

    // Command Declarations (Partial)
    public ReactiveCommand<Unit, Unit> AIScanCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> TriggerAutoScanCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleAIScanBoxCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> TranslateAllSelectionsCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ScanAllTextCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearAllSelectionsCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleTranslationSelectCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleAutoDetectCommand { get; set; } = null!;
    public ReactiveCommand<UserSelectionRect, Unit> RemoveUserSelectionCommand { get; set; } = null!;
    public ReactiveCommand<TranslationTool, Unit> SelectTranslationToolCommand { get; set; } = null!;
    public ReactiveCommand<UserSelectionRect, Unit> CopyTranslationTextCommand { get; set; } = null!;
    public ReactiveCommand<object?, Unit> PinTranslationCommand { get; set; } = null!;

    private void InitializeSelectionCommands()
    {
        AIScanCommand = ReactiveCommand.CreateFromTask(RunAIScanAsync);
        AIScanCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"AI Scan Command error: {ex}"));

        // Keep SAM2 path for manual/advanced trigger.
        TriggerAutoScanCommand = ReactiveCommand.CreateFromTask(RunSAM2ScanAsync);
        TriggerAutoScanCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Manual SAM2 Scan Command error: {ex}"));

        ToggleAIScanBoxCommand = ReactiveCommand.Create(() => { ShowAIScanBox = !ShowAIScanBox; return Unit.Default; });
        ToggleAIScanBoxCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Toggle AI Box error: {ex}"));


        var canExecuteInTranslation = this.WhenAnyValue(x => x.CurrentMode, mode => mode == SnipMode.Translation);

        TranslateAllSelectionsCommand = ReactiveCommand.Create(() => { _ = TranslateAllSelectionsAsync(); }, canExecuteInTranslation);
        TranslateAllSelectionsCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"TranslateAll error: {ex}"));

        ScanAllTextCommand = ReactiveCommand.CreateFromTask(ScanAllTextAsync, canExecuteInTranslation);
        ScanAllTextCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"ScanAll error: {ex}"));

        ClearAllSelectionsCommand = ReactiveCommand.Create(() => UserSelections.Clear(), canExecuteInTranslation);
        ClearAllSelectionsCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"ClearAll error: {ex}"));

        ToggleTranslationSelectCommand = ReactiveCommand.Create(() => { IsTranslationSelectionActive = !IsTranslationSelectionActive; }, canExecuteInTranslation);
        ToggleTranslationSelectCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"ToggleSelect error: {ex}"));

        ToggleAutoDetectCommand = ReactiveCommand.Create(() => { IsGlobalAutoDetectEnabled = !IsGlobalAutoDetectEnabled; }, canExecuteInTranslation);
        ToggleAutoDetectCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"ToggleAutoDetect error: {ex}"));
        
        SelectTranslationToolCommand = ReactiveCommand.Create<TranslationTool>(tool => 
        {
            CurrentTranslationTool = tool;
        });
        SelectTranslationToolCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Select Tool error: {ex}"));

        RemoveUserSelectionCommand = ReactiveCommand.Create<UserSelectionRect>(item => { UserSelections.Remove(item); });
        RemoveUserSelectionCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"RemoveSelection error: {ex}"));

        CopyTranslationTextCommand = ReactiveCommand.CreateFromTask<UserSelectionRect>(async (UserSelectionRect item) => 
        {
            if (item != null && !string.IsNullOrEmpty(item.TranslatedText))
            {
                await _captureService.CopyToClipboardAsync(item.TranslatedText);
                _mainVm?.SetStatus("StatusCopied");
            }
        });
        CopyTranslationTextCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Copy Translation error: {ex}"));

        PinTranslationCommand = ReactiveCommand.CreateFromTask<object?>(PinTranslationAsync);
        PinTranslationCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Pin Translation error: {ex}"));

        // 監聽集合變更以即時更新遮罩挖空，並訂閱項目的屬性變更
        UserSelections.CollectionChanged += (s, e) => 
        {
            if (e.NewItems != null)
            {
                foreach (UserSelectionRect item in e.NewItems)
                {
                    item.WhenAnyValue(x => x.Bounds, x => x.IsTranslated)
                        .Subscribe(_ => UpdateMask());
                }
            }
            UpdateMask();
        };
    }

    private SAM2Service? _sam2Service;

    private void InitializeSAM2(MainWindowViewModel mainVm)
    {
        _sam2Service = new SAM2Service(mainVm.AIResourceService, mainVm.AppSettingsService);
        Task.Run(async () => 
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[SAM2 Preload] Starting background initialization and warmup...");
                await _sam2Service.InitializeAsync();
                System.Diagnostics.Debug.WriteLine("[SAM2 Preload] Background warmup complete. Ready for scan.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SAM2 Preload] Failed: {ex.Message}");
            }
        });
    }
}
