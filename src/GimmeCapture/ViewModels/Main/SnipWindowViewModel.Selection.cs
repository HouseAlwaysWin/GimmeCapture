using Avalonia;
using Avalonia.Media;
using GimmeCapture.Models;
using ReactiveUI;
using System;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    private SnipState _currentState = SnipState.Detecting;
    public SnipState CurrentState
    {
        get => _currentState;
        set
        {
            System.Diagnostics.Debug.WriteLine($"[SnipState] {_currentState} -> {value}");
            this.RaiseAndSetIfChanged(ref _currentState, value);
            
            // If we leave Detecting state (e.g. start selecting), cancel any running scan
            if (value != SnipState.Detecting)
            {
                _scanCts?.Cancel();
                // Optional: clear rects immediately if we want them gone 
                // (though SnipWindow.axaml handles visibility too)
            }
            else
            {
                // Re-entering Detecting state (e.g. from Cancel/Reset)
                // Restart scan if enabled (only after AllScreenBounds is populated)
                if (ShowAIScanBox && AllScreenBounds?.Count > 0)
                {
                    TriggerAutoScanCommand?.Execute(Unit.Default).Subscribe();
                }
            }

            if (value == SnipState.Selected)
            {
                TriggerAutoAction();
                
                // Clear translated blocks when selection changes
                TranslatedBlocks.Clear();
            }
        }
    }


    private bool _showSnipToolBar;
    public bool ShowSnipToolBar
    {
        get => _showSnipToolBar;
        set => this.RaiseAndSetIfChanged(ref _showSnipToolBar, value);
    }

    private bool _showTopLoadingBar;
    public bool ShowTopLoadingBar
    {
        get => _showTopLoadingBar;
        set => this.RaiseAndSetIfChanged(ref _showTopLoadingBar, value);
    }

    private bool _showProcessingOverlay;
    public bool ShowProcessingOverlay
    {
        get => _showProcessingOverlay;
        set => this.RaiseAndSetIfChanged(ref _showProcessingOverlay, value);
    }

    private string _processingText = LocalizationService.Instance["StatusProcessing"];
    public string ProcessingText
    {
        get => _processingText;
        set => this.RaiseAndSetIfChanged(ref _processingText, value);
    }
    
    private bool _isIndeterminate = false;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => this.RaiseAndSetIfChanged(ref _isIndeterminate, value);
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set => this.RaiseAndSetIfChanged(ref _progressValue, value);
    }

    // Feature Flags (Synced)


    private bool _showAIScanBox;
    public bool ShowAIScanBox
    {
        get => _showAIScanBox;
        set 
        {
            this.RaiseAndSetIfChanged(ref _showAIScanBox, value);
            if (_mainVm != null) 
            {
                _mainVm.ShowAIScanBox = value;
                
                if (!value)
                {
                    WindowRects.Clear();
                    _scanCts?.Cancel();
                }
                else
                {
                    // Trigger scan if enabled (only after AllScreenBounds is populated)
                    if (CurrentState == SnipState.Detecting && AllScreenBounds?.Count > 0)
                    {
                        TriggerAutoScanCommand?.Execute(Unit.Default).Subscribe();
                    }
                }
            }
        }
    }

    private bool _enableAIScan;
    public bool EnableAIScan
    {
        get => _enableAIScan;
        set 
        {
            this.RaiseAndSetIfChanged(ref _enableAIScan, value);
            if (_mainVm != null) _mainVm.EnableAIScan = value;
        }
    }

    // Restore Missing Properties
    private bool _isTranslationActive;
    public bool IsTranslationActive
    {
        get => _isTranslationActive;
        set
        {
            System.Diagnostics.Debug.WriteLine($"[Translation] IsTranslationActive -> {value}");
            this.RaiseAndSetIfChanged(ref _isTranslationActive, value);
            // Just toggle visibility, the actual translation is triggered by TranslateCommand
            if (!value)
            {
                TranslatedBlocks.Clear();
            }
        }
    }

    public ObservableCollection<TranslatedBlock> TranslatedBlocks { get; } = new();
    private TranslationService? _translationService;
    private CancellationTokenSource? _translationCts;
    private int _translationVersion = 0;
    private DateTime _lastTranslationRequestAt = DateTime.MinValue;
    private Rect _lastTranslationRect = new Rect(0, 0, 0, 0);

    private Rect _selectionRect;
    public Rect SelectionRect
    {
        get => _selectionRect;
        set 
        {
            this.RaiseAndSetIfChanged(ref _selectionRect, value);
            UpdateMask();
            UpdateToolbarPosition();
        }
    }

    private Geometry _maskGeometry = new GeometryGroup();
    public Geometry MaskGeometry
    {
        get => _maskGeometry;
        set => this.RaiseAndSetIfChanged(ref _maskGeometry, value);
    }

    private void UpdateMask()
    {
        MaskGeometry = new CombinedGeometry
        {
            GeometryCombineMode = GeometryCombineMode.Exclude,
            Geometry1 = new RectangleGeometry(new Rect(-10000, -10000, 20000, 20000)),
            Geometry2 = new RectangleGeometry(SelectionRect)
        };
    }

    private double _maskOpacity = 0.5;
    public double MaskOpacity
    {
        get => _maskOpacity;
        set => this.RaiseAndSetIfChanged(ref _maskOpacity, value);
    }

    private Color _selectionBorderColor = Colors.Red;
    public Color SelectionBorderColor
    {
        get => _selectionBorderColor;
        set => this.RaiseAndSetIfChanged(ref _selectionBorderColor, value);
    }

    private double _selectionBorderThickness = 2.0;
    public double SelectionBorderThickness
    {
        get => _selectionBorderThickness;
        set => this.RaiseAndSetIfChanged(ref _selectionBorderThickness, value);
    }

    private bool _isMagnifierEnabled = true;
    public bool IsMagnifierEnabled
    {
        get => _isMagnifierEnabled;
        set => this.RaiseAndSetIfChanged(ref _isMagnifierEnabled, value);
    }

    private PixelPoint _screenOffset;
    public PixelPoint ScreenOffset
    {
        get => _screenOffset;
        set => this.RaiseAndSetIfChanged(ref _screenOffset, value);
    }

    private double _visualScaling = 1.0;
    public double VisualScaling
    {
        get => _visualScaling;
        set => this.RaiseAndSetIfChanged(ref _visualScaling, value);
    }

    private Size _viewportSize;
    public Size ViewportSize
    {
        get => _viewportSize;
        set 
        {
            this.RaiseAndSetIfChanged(ref _viewportSize, value);
            UpdateToolbarPosition();
        }
    }

    private double _toolbarTop;
    public double ToolbarTop
    {
        get => _toolbarTop;
        set => this.RaiseAndSetIfChanged(ref _toolbarTop, value);
    }

    private double _toolbarLeft;
    public double ToolbarLeft
    {
        get => _toolbarLeft;
        set => this.RaiseAndSetIfChanged(ref _toolbarLeft, value);
    }

    private double _toolbarWidth;
    public double ToolbarWidth
    {
        get => _toolbarWidth;
        set => this.RaiseAndSetIfChanged(ref _toolbarWidth, value);
    }

    private double _toolbarHeight;
    public double ToolbarHeight
    {
        get => _toolbarHeight;
        set => this.RaiseAndSetIfChanged(ref _toolbarHeight, value);
    }

    private double _translationOverlayTop;
    public double TranslationOverlayTop
    {
        get => _translationOverlayTop;
        set => this.RaiseAndSetIfChanged(ref _translationOverlayTop, value);
    }

    private bool _isTranslationOverlayManuallyPositioned;
    public bool IsTranslationOverlayManuallyPositioned
    {
        get => _isTranslationOverlayManuallyPositioned;
        set => this.RaiseAndSetIfChanged(ref _isTranslationOverlayManuallyPositioned, value);
    }

    private double _translationOverlayLeft;
    public double TranslationOverlayLeft
    {
        get => _translationOverlayLeft;
        set => this.RaiseAndSetIfChanged(ref _translationOverlayLeft, value);
    }

    public double ToolbarMaxWidth => Math.Max(ViewportSize.Width - 40, 100);

    private void UpdateToolbarPosition()
    {
        // Default viewport fallback
        double vh = ViewportSize.Height > 0 ? ViewportSize.Height : 1080;
        double vw = ViewportSize.Width > 0 ? ViewportSize.Width : 1920;

        // Use live measured bounds. Add buffer for shadow/border.
        double tw = ToolbarWidth > 0 ? (ToolbarWidth + 20) : 600;
        double th = ToolbarHeight > 0 ? ToolbarHeight : 45;

        // Position below by default
        double top = SelectionRect.Bottom + 12; 
        double left = SelectionRect.Left;

        // Multi-monitor clamping: Find which monitor the selection is mostly on
        var targetMonitor = AllScreenBounds?.FirstOrDefault(s => 
            new Rect(s.X, s.Y, s.W, s.H).Intersects(SelectionRect)) 
            ?? new ScreenBoundsViewModel { X = 0, Y = 0, W = vw, H = vh };

        double monitorLeft = targetMonitor.X;
        double monitorTop = targetMonitor.Y;
        double monitorRight = targetMonitor.X + targetMonitor.W;
        double monitorBottom = targetMonitor.Y + targetMonitor.H;

        // If bottom overflows monitor, position above selection
        if (top + th > monitorBottom - 10)
        {
            top = SelectionRect.Top - th - 12;
        }

        // Horizontal Clamping to monitor bounds
        if (left + tw > monitorRight - 20)
        {
            left = monitorRight - tw - 20;
        }
        if (left < monitorLeft + 20)
        {
            left = monitorLeft + 20;
        }

        // Vertical Clamping to monitor bounds
        if (top < monitorTop + 10)
        {
            top = monitorTop + 10;
        }
        if (top + th > monitorBottom - 10)
        {
            top = monitorBottom - th - 10;
        }

        ToolbarTop = top;
        ToolbarLeft = left;
        
        // Ensure MaxWidth allows full toolbar on smaller monitors
        this.RaisePropertyChanged(nameof(ToolbarMaxWidth));
       
        // Default to positioning translation below the toolbar
        if (!IsTranslationOverlayManuallyPositioned)
        {
            TranslationOverlayTop = top + th + 8;
            TranslationOverlayLeft = left;
        }
    }


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
    public ReactiveCommand<Unit, Unit> TranslateCommand { get; set; } = null!;

    private void InitializeSelectionCommands()
    {
        AIScanCommand = ReactiveCommand.CreateFromTask(RunAIScanAsync);
        AIScanCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"AI Scan Command error: {ex}"));

        TriggerAutoScanCommand = ReactiveCommand.CreateFromTask(RunAIScanAsync);
        TriggerAutoScanCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Auto Scan Command error: {ex}"));

        ToggleAIScanBoxCommand = ReactiveCommand.Create(() => { ShowAIScanBox = !ShowAIScanBox; return Unit.Default; });
        ToggleAIScanBoxCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Toggle AI Box error: {ex}"));

        TranslateCommand = ReactiveCommand.CreateFromTask(PerformTranslationAsync);
        TranslateCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Translate Command error: {ex}"));
    }

    private SAM2Service? _sam2Service;

    private void InitializeSAM2(MainWindowViewModel mainVm)
    {
        _sam2Service = new SAM2Service(mainVm.AIResourceService, mainVm.AppSettingsService);
        Task.Run(async () => 
        {
            try
            {
                Console.WriteLine("[SAM2 Preload] Starting background initialization and warmup...");
                await _sam2Service.InitializeAsync();
                Console.WriteLine("[SAM2 Preload] Background warmup complete. Ready for scan.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SAM2 Preload] Failed: {ex.Message}");
            }
        });
    }

    private async Task PerformTranslationAsync()
    {
        Console.WriteLine("[Translation] PerformTranslationAsync triggered");
        
        if (CurrentState != SnipState.Selected || SelectionRect.Width <= 10 || SelectionRect.Height <= 10)
        {
            return;
        }

        // 0. Update UI state immediately for responsiveness
        IsTranslationActive = true;
        ShowSnipToolBar = true;
        ProcessingText = LocalizationService.Instance["StatusTranslating"] ?? "Translating...";
        IsIndeterminate = true;
        ProgressValue = 0;

        if (_translationService == null)
        {
            if (_mainVm?.AIResourceService == null) return;
            _translationService = new TranslationService(_mainVm.AIResourceService, _mainVm.AppSettingsService, _mainVm.MarianMTService);
        }

        // Ensure translation uses the latest values from Settings UI.
        if (_mainVm != null)
        {
            _mainVm.AppSettingsService.Settings.TargetLanguage = _mainVm.TargetLanguage;
            _mainVm.AppSettingsService.Settings.SourceLanguage = _mainVm.SourceLanguage;
            Console.WriteLine($"[Translation] Effective languages => OCR:{_mainVm.SourceLanguage}, Target:{_mainVm.TargetLanguage}");
        }

        // Debounce repeated trigger storms from UI/events.
        var now = DateTime.UtcNow;
        if (now - _lastTranslationRequestAt < TimeSpan.FromMilliseconds(500) &&
            AreRectsSimilar(_lastTranslationRect, SelectionRect))
        {
            Console.WriteLine("[Translation] Ignored duplicated trigger (debounced)");
            return;
        }
        _lastTranslationRequestAt = now;
        _lastTranslationRect = SelectionRect;

        var currentVersion = Interlocked.Increment(ref _translationVersion);
        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = new CancellationTokenSource();
        var token = _translationCts.Token;

        // 1. Engine specific readiness check
        var settings = _mainVm?.AppSettingsService.Settings ?? new AppSettings();
        if (settings.SelectedTranslationEngine == TranslationEngine.Ollama)
        {
            var models = await _translationService.GetAvailableModelsAsync();
            if (!models.Any())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    ShowSnipToolBar = true;
                    ProcessingText = LocalizationService.Instance["StatusOllamaRequired"] ?? "Please install Ollama and download a model first.";
                    IsIndeterminate = false;
                    ProgressValue = 100;
                });
                await Task.Delay(3000);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    ShowSnipToolBar = false;
                    IsTranslationActive = false;
                });
                return;
            }
        }
        else if (settings.SelectedTranslationEngine == TranslationEngine.MarianMT)
        {
            if (_mainVm != null && !_mainVm.AIResourceService.IsNmtReady())
            {
                if (_mainVm.ConfirmAction != null)
                {
                    var confirmed = await _mainVm.ConfirmAction(
                        LocalizationService.Instance["AIDownloadTitle"],
                        LocalizationService.Instance["MarianMTDownloadConfirm"]);
                    
                    if (confirmed)
                    {
                        // 1. Close SnipWindow immediately
                        Close();

                        // 2. Trigger background download in MainWindow (don't await so we can finish this task)
                        _ = _mainVm.InstallModuleAsync("MarianMT");
                        return;
                    }
                    else
                    {
                        IsTranslationActive = false;
                        ShowSnipToolBar = false;
                        return;
                    }
                }
            }
            
            // Final check
            if (_mainVm != null && !_mainVm.AIResourceService.IsNmtReady())
            {
                 ProcessingText = LocalizationService.Instance["StatusMarianMTNotReady"] ?? "Offline translation resources not ready.";
                 await Task.Delay(2000);
                 IsTranslationActive = false;
                 ShowSnipToolBar = false;
                 return;
            }
        }

        try
        {
            token.ThrowIfCancellationRequested();
            using (var bitmap = await _captureService.CaptureScreenAsync(SelectionRect, ScreenOffset, VisualScaling, false))
            {
                if (bitmap != null)
                {
                    token.ThrowIfCancellationRequested();
                    // Check and Ensure OCR resources are ready
                    if (_mainVm != null)
                    {
                        bool ready = await _mainVm.AIResourceService.EnsureOCRAsync();
                        if (!ready)
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                                ShowSnipToolBar = true;
                                ProcessingText = LocalizationService.Instance["StatusOCRNotReady"] ?? "OCR resources not ready.";
                                IsIndeterminate = false;
                                ProgressValue = 100;
                            });
                            await Task.Delay(3000);
                            return;
                        }
                    }

                    // Proceed with actual analysis and translation
                    ProcessingText = LocalizationService.Instance["StatusTranslating"] ?? "Translating...";
                    IsIndeterminate = true;
                    
                    // Task.Run to offload CPU-intensive OCR from UI thread
                    var blocks = await Task.Run(() => _translationService.AnalyzeAndTranslateAsync(bitmap, token), token);
                    
                    if (currentVersion != _translationVersion || token.IsCancellationRequested) return;
                    
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (currentVersion != _translationVersion || token.IsCancellationRequested) return;
                        TranslatedBlocks.Clear();
                        foreach (var block in blocks)
                        {
                            TranslatedBlocks.Add(block);
                        }
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[Translation] Request cancelled");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Translation] Error: {ex}");
            var errorFmt = LocalizationService.Instance["StatusTranslationError"] ?? "Translation Error: {0}";
            ProcessingText = string.Format(errorFmt, ex.Message);
            IsIndeterminate = false;
            ProgressValue = 100;
            await Task.Delay(3000);
        }
        finally
        {
            if (currentVersion == _translationVersion)
            {
                // Add a small delay so the user can see the "Translating..." status 
                // and result before the toolbar disappears.
                await Task.Delay(500);
                ShowSnipToolBar = false;
                IsTranslationActive = false;
            }
        }
    }

    private static bool AreRectsSimilar(Rect a, Rect b)
    {
        const double tol = 2.0;
        return Math.Abs(a.X - b.X) <= tol &&
               Math.Abs(a.Y - b.Y) <= tol &&
               Math.Abs(a.Width - b.Width) <= tol &&
               Math.Abs(a.Height - b.Height) <= tol;
    }
}
