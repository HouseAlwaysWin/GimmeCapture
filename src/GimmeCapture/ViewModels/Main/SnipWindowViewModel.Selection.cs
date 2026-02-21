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
using GimmeCapture.Services.OCR;

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
            this.RaisePropertyChanged(nameof(SelectionShadowColor));
            
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
                // 翻譯模式不使用 SAM2 掃描
                if (ShowAIScanBox && !IsTranslationMode && AllScreenBounds?.Count > 0)
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
            
            this.RaisePropertyChanged(nameof(IsToolbarVisible));
            UpdateMask();
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
                    // 翻譯模式不使用 SAM2
                    if (CurrentState == SnipState.Detecting && !IsTranslationMode && AllScreenBounds?.Count > 0)
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

        }
    }

    public ObservableCollection<TranslatedBlock> TranslatedBlocks { get; } = new();
    public ObservableCollection<UserSelectionRect> UserSelections { get; } = new();
    private TranslationService? _translationService;
    private CancellationTokenSource? _translationCts;


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

    // Auto-Detect OCR Monitor Loop
    private CancellationTokenSource? _autoDetectCts;
    private Task? _autoDetectTask;
    private GimmeCapture.Services.OCR.PaddleOCREngine? _sharedOcrEngine;

    public void StartAutoDetectLoop()
    {
        StopAutoDetectLoop();
        _autoDetectCts = new CancellationTokenSource();
        _autoDetectTask = Task.Run(() => AutoDetectLoopAsync(_autoDetectCts.Token));
    }

    public void StopAutoDetectLoop()
    {
        _autoDetectCts?.Cancel();
        _autoDetectCts?.Dispose();
        _autoDetectCts = null;
        
        _sharedOcrEngine?.Dispose();
        _sharedOcrEngine = null;
    }

    private async Task AutoDetectLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500, token); // Check every 1.5 seconds

                if (_mainVm == null || !IsTranslationMode) continue;
                bool isOcrReady = await _mainVm.AIResourceService.EnsureOCRAsync();
                if (!isOcrReady) continue;

                if (_sharedOcrEngine == null)
                {
                    _sharedOcrEngine = new GimmeCapture.Services.OCR.PaddleOCREngine(_mainVm.AIResourceService, _mainVm.AppSettingsService);
                }

                var activeSections = UserSelections.Where(s => s.IsAutoDetectEnabled).ToList();
                if (activeSections.Count == 0) continue;

                var ocrLang = _mainVm.AppSettingsService.Settings.SourceLanguage;
                // Auto detect script if source is auto
                if (ocrLang == OCRLanguage.Auto)
                {
                    // Fallback to Trad Chinese or Japanese based on some heuristic or just default to Trad Chinese for now
                    // For better auto-detect, we might need a fast script detector
                    ocrLang = OCRLanguage.TraditionalChinese;
                }

                await _sharedOcrEngine.EnsureLoadedAsync(ocrLang, token);

                foreach (var sel in activeSections)
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        var rect = sel.Bounds;
                        if (rect.Width <= 10 || rect.Height <= 10) continue;

                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsCapturing = true);
                        await Task.Delay(50); // 等待 UI 隱藏

                        using var bitmap = await _captureService.CaptureScreenAsync(rect, ScreenOffset, VisualScaling, false);
                        
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsCapturing = false);

                        if (bitmap == null) continue;

                        // Because the region is already cropped to the box, we can just recognize the whole thing.
                        // Or we can just use TranslationService's AnalyzeAndTranslateAsync which does OCR + Translation in one go,
                        // but that might be heavy if the text hasn't changed.
                        // Let's use DetectText + RecognizeText to get the text string to compare first.
                        var boxes = _sharedOcrEngine.DetectText(bitmap);
                        var sb = new System.Text.StringBuilder();
                        foreach (var box in boxes)
                        {
                            var ocrResult = _sharedOcrEngine.RecognizeText(bitmap, box, token);
                            if (!string.IsNullOrWhiteSpace(ocrResult.text))
                            {
                                sb.AppendLine(ocrResult.text);
                            }
                        }
                        var newText = sb.ToString().Trim();

                        if (!string.IsNullOrWhiteSpace(newText) && newText != sel.LastOcrText)
                        {
                            System.Diagnostics.Debug.WriteLine($"[AutoDetect] Text changed for a selection. Old length: {sel.LastOcrText?.Length}, New length: {newText.Length}");
                            
                            // Ask TranslationService to translate just this text
                            if (_translationService != null)
                            {
                                // We don't want to freeze the UI or show loading bars for background updates
                                var blocks = await _translationService.AnalyzeAndTranslateAsync(bitmap, VisualScaling, token);
                                var combinedText = string.Join("\n", blocks.Select(b => b.TranslatedText).Where(t => !string.IsNullOrWhiteSpace(t)));
                                
                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    sel.LastOcrText = newText; // Update tracking hash
                                    sel.TranslatedText = combinedText;
                                    sel.IsTranslated = !string.IsNullOrWhiteSpace(combinedText);
                                    
                                    // Propagate inferred font size from blocks
                                    if (blocks.Any())
                                    {
                                        sel.InferredFontSize = blocks[0].InferredFontSize;
                                    }

                                    if (sel.IsTranslated) sel.EstimatedTextHeight = EstimateTranslatedTextHeight(sel);
                                    UpdateMask();
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AutoDetect] Region OCR error: {ex.Message}");
                    }
                }
                
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AutoDetect] Loop Error: {ex.Message}");
            }
        }
    }

    private Geometry _maskGeometry = new GeometryGroup();
    public Geometry MaskGeometry
    {
        get => _maskGeometry;
        set => this.RaiseAndSetIfChanged(ref _maskGeometry, value);
    }

    public void UpdateMask()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateMask);
            return;
        }

        // 1. 建立基礎背景幾何 (覆蓋目前的視口區域)
        double w = ViewportSize.Width > 0 ? ViewportSize.Width : 5000;
        double h = ViewportSize.Height > 0 ? ViewportSize.Height : 5000;
        Geometry mainMask = new RectangleGeometry(new Rect(-100, -100, w + 200, h + 200));

        // 2. 處理截圖模式選取框 (全挖空)
        if (SelectionRect.Width > 0 && SelectionRect.Height > 0 && !IsTranslationMode)
        {
            if (CurrentState == SnipState.Selected)
            {
                // V8: When the box is set (Selected), remove the full screen mask
                mainMask = new GeometryGroup();
            }
            else
            {
                mainMask = new CombinedGeometry
                {
                    GeometryCombineMode = GeometryCombineMode.Exclude,
                    Geometry1 = mainMask,
                    Geometry2 = new RectangleGeometry(SelectionRect)
                };
            }
        }

        // 3. 處理翻譯模式選取框 (V8: 已翻譯不挖洞，與 Win32 Region 同步)
        if (IsTranslationMode)
        {
            if (!IsTranslationSelectionActive)
            {
                // V8: Edit Mode - The user wants to click through the OTHER parts of the screen!
                // So the entire screen should be click-through, and NO FULL SCREEN MASK.
                mainMask = new GeometryGroup(); 
            }
            else
            {
                foreach (var sel in UserSelections)
                {
                    if (sel.Bounds.Width > 10 && sel.Bounds.Height > 10)
                    {
                        var rect = sel.Bounds;

                        // 翻譯模式無論是否已完成，一律排除遮罩以保持通亮
                        mainMask = new CombinedGeometry
                        {
                            GeometryCombineMode = GeometryCombineMode.Exclude,
                            Geometry1 = mainMask,
                            Geometry2 = new RectangleGeometry(rect)
                        };
                    }
                }
            }
        }

        MaskGeometry = mainMask;
        System.Diagnostics.Debug.WriteLine($"[Mask] UpdateMask (V8) done. Viewport: {w}x{h}");
    }

    private double _maskOpacity = 0.5;
    public double MaskOpacity
    {
        get => IsTranslationMode ? 0.0 : _maskOpacity;
        set 
        {
            this.RaiseAndSetIfChanged(ref _maskOpacity, value);
            this.RaisePropertyChanged(nameof(MaskOpacity));
        }
    }

    private Color _selectionBorderColor = Colors.Transparent;
    public Color SelectionBorderColor
    {
        get => _selectionBorderColor;
        set 
        {
            this.RaiseAndSetIfChanged(ref _selectionBorderColor, value);
            this.RaisePropertyChanged(nameof(SelectionShadowColor));
        }
    }

    public Color SelectionShadowColor => CurrentState == SnipState.Selected ? Colors.Transparent : SelectionBorderColor;

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
            UpdateMask();
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

    public double ToolbarMaxWidth => (ViewportSize.Width > 100) ? ViewportSize.Width - 40 : 2000;

    private bool _isToolbarManuallyPositioned;
    public bool IsToolbarManuallyPositioned
    {
        get => _isToolbarManuallyPositioned;
        set => this.RaiseAndSetIfChanged(ref _isToolbarManuallyPositioned, value);
    }

    private TranslationTool _currentTranslationTool = TranslationTool.Select;
    public TranslationTool CurrentTranslationTool
    {
        get => _currentTranslationTool;
        set 
        {
            this.RaiseAndSetIfChanged(ref _currentTranslationTool, value);
            this.RaisePropertyChanged(nameof(IsTranslationSelectionActive));
            UpdateMask();
        }
    }

    public bool IsTranslationSelectionActive
    {
        get => CurrentTranslationTool == TranslationTool.Select;
        set => CurrentTranslationTool = value ? TranslationTool.Select : TranslationTool.Edit;
    }

    // 翻譯工具列位置（可拖曳，預設螢幕中間上方）
    private double _translationToolbarTop = 20;
    public double TranslationToolbarTop
    {
        get => _translationToolbarTop;
        set => this.RaiseAndSetIfChanged(ref _translationToolbarTop, value);
    }

    private double _translationToolbarLeft = -1; // -1 = auto center
    public double TranslationToolbarLeft
    {
        get => _translationToolbarLeft;
        set => this.RaiseAndSetIfChanged(ref _translationToolbarLeft, value);
    }

    public void InitializeTranslationToolbarPosition()
    {
        // 如果使用者已經手動移動過，且還在同一個螢幕範圍內，可能不需要強行置中
        // 但目前的邏輯是：只有在尚未手動移動時才自動置中
        if (IsToolbarManuallyPositioned && IsTranslationMode)
        {
            return;
        }

        // 根據滑鼠所在的螢幕 (ActiveScreenBounds) 置中工具列
        Rect bounds = ActiveScreenBounds.Width > 0 ? ActiveScreenBounds : new Rect(0, 0, ViewportSize.Width > 0 ? ViewportSize.Width : 1920, ViewportSize.Height > 0 ? ViewportSize.Height : 1080);
        
        double tw = ToolbarWidth > 0 ? ToolbarWidth : 200;
        TranslationToolbarLeft = bounds.X + (bounds.Width - tw) / 2;
        TranslationToolbarTop = bounds.Y + 20;

        // 同步更新 XAML 綁定的工具列位置
        ToolbarLeft = TranslationToolbarLeft;
        ToolbarTop = TranslationToolbarTop;
    }

    private void UpdateToolbarPosition()
    {
        // 翻譯模式下工具列位置由 InitializeTranslationToolbarPosition 管理
        if (IsTranslationMode) return;

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
    public ReactiveCommand<Unit, Unit> TranslateAllSelectionsCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ScanAllTextCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearAllSelectionsCommand { get; set; } = null!;
    public ReactiveCommand<UserSelectionRect, Unit> RemoveUserSelectionCommand { get; set; } = null!;
    public ReactiveCommand<TranslationTool, Unit> SelectTranslationToolCommand { get; set; } = null!;
    public ReactiveCommand<UserSelectionRect, Unit> CopyTranslationTextCommand { get; set; } = null!;

    private void InitializeSelectionCommands()
    {
        AIScanCommand = ReactiveCommand.CreateFromTask(RunAIScanAsync);
        AIScanCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"AI Scan Command error: {ex}"));

        TriggerAutoScanCommand = ReactiveCommand.CreateFromTask(RunAIScanAsync);
        TriggerAutoScanCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Auto Scan Command error: {ex}"));

        ToggleAIScanBoxCommand = ReactiveCommand.Create(() => { ShowAIScanBox = !ShowAIScanBox; return Unit.Default; });
        ToggleAIScanBoxCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Toggle AI Box error: {ex}"));


        // 翻譯模式專用命令
        TranslateAllSelectionsCommand = ReactiveCommand.CreateFromTask(TranslateAllSelectionsAsync);
        TranslateAllSelectionsCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"TranslateAll error: {ex}"));

        ScanAllTextCommand = ReactiveCommand.CreateFromTask(ScanAllTextAsync);
        ScanAllTextCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"ScanAll error: {ex}"));

        ClearAllSelectionsCommand = ReactiveCommand.Create(() => UserSelections.Clear());
        ClearAllSelectionsCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"ClearAll error: {ex}"));
        
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



    /// <summary>
    /// 翻譯模式：翻譯所有 UserSelections 中的圈選區域
    /// </summary>
    private async Task TranslateAllSelectionsAsync()
    {
        if (UserSelections.Count == 0) return;

        System.Diagnostics.Debug.WriteLine($"[TranslationMode] TranslateAllSelections: {UserSelections.Count} regions");

        ShowTopLoadingBar = true;
        IsIndeterminate = true;

        if (_translationService == null)
        {
            if (_mainVm?.AIResourceService == null) return;
            _translationService = new TranslationService(_mainVm.AIResourceService, _mainVm.AppSettingsService, _mainVm.MarianMTService);
        }

        // Sync language settings
        if (_mainVm != null)
        {
            _mainVm.AppSettingsService.Settings.TargetLanguage = _mainVm.TargetLanguage;
            _mainVm.AppSettingsService.Settings.SourceLanguage = _mainVm.SourceLanguage;
        }

        _translationCts?.Cancel();
        _translationCts?.Dispose();
        _translationCts = new CancellationTokenSource();
        var token = _translationCts.Token;

        // Ensure OCR resources
        if (_mainVm != null)
        {
            bool ready = await _mainVm.AIResourceService.EnsureOCRAsync();
            if (!ready)
            {
                System.Diagnostics.Debug.WriteLine("[TranslationMode] OCR not ready");
                ShowTopLoadingBar = false;
                IsIndeterminate = false;
                return;
            }
        }

        try
        {
            // 逐一翻譯每個選取區域
            var selectionsCopy = UserSelections.ToList();
            foreach (var sel in selectionsCopy)
            {
                if (token.IsCancellationRequested) break;
                // 用戶點擊按鈕時，無論是否翻譯過都強制重新翻譯

                IsCapturing = true;
                await Task.Delay(50); // 等待 UI 隱藏

                using var bitmap = await _captureService.CaptureScreenAsync(sel.Bounds, ScreenOffset, VisualScaling, false);
                
                IsCapturing = false;

                if (bitmap == null) continue;

                token.ThrowIfCancellationRequested();
                var blocks = await Task.Run(() => _translationService.AnalyzeAndTranslateAsync(bitmap, VisualScaling, token), token);
                
                if (token.IsCancellationRequested) break;

                // 合併所有翻譯結果作為這個區域的翻譯文字
                var combinedText = string.Join("\n", blocks.Select(b => b.TranslatedText).Where(t => !string.IsNullOrWhiteSpace(t)));
                
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    sel.TranslatedText = combinedText;
                    sel.IsTranslated = !string.IsNullOrWhiteSpace(combinedText);

                    // Propagate inferred font size from blocks
                    if (blocks.Any())
                    {
                        sel.InferredFontSize = blocks[0].InferredFontSize;
                    }

                    if (sel.IsTranslated)
                    {
                        sel.EstimatedTextHeight = EstimateTranslatedTextHeight(sel);
                    }

                    // V8: 翻譯後重新整理遮罩和 Win32 Region
                    // 因為 IsTranslated 不在 WhenAnyValue 訂閱中，必須手動觸發
                    UpdateMask();
                });
            }
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("[TranslationMode] TranslateAll cancelled");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TranslationMode] TranslateAll error: {ex}");
        }
        finally
        {
            ShowTopLoadingBar = false;
            IsIndeterminate = false;
        }
    }

    /// <summary>
    /// 翻譯模式：PaddleOCR 掃描全螢幕偵測可翻譯文字區域
    /// </summary>
    private async Task ScanAllTextAsync()
    {
        System.Diagnostics.Debug.WriteLine("[TranslationMode] ScanAllText triggered");

        ShowTopLoadingBar = true;
        IsIndeterminate = true;

        if (_mainVm?.AIResourceService == null) return;

        // Ensure OCR resources
        bool ready = await _mainVm.AIResourceService.EnsureOCRAsync();
        if (!ready)
        {
            System.Diagnostics.Debug.WriteLine("[TranslationMode] OCR not ready for scan");
            ShowTopLoadingBar = false;
            IsIndeterminate = false;
            return;
        }

        try
        {
            // 擷取全螢幕
            var fullScreenRect = new Rect(0, 0, ViewportSize.Width, ViewportSize.Height);
            using var bitmap = await _captureService.CaptureScreenAsync(fullScreenRect, ScreenOffset, VisualScaling, false);
            if (bitmap == null) return;

            // 使用 PaddleOCR 偵測文字區域
            var ocrEngine = new PaddleOCREngine(_mainVm.AIResourceService, _mainVm.AppSettingsService);
            var ocrLang = _mainVm.AppSettingsService.Settings.SourceLanguage;
            await ocrEngine.EnsureLoadedAsync(ocrLang);
            
            var textBoxes = await Task.Run(() => ocrEngine.DetectText(bitmap));

            System.Diagnostics.Debug.WriteLine($"[TranslationMode] Found {textBoxes.Count} text regions");

            // 將偵測到的文字區域轉換為 UserSelectionRect
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                UserSelections.Clear();

                // 計算工具列區域（用於排除）
                double tbLeft = TranslationToolbarLeft >= 0 ? TranslationToolbarLeft : (ViewportSize.Width - (ToolbarWidth > 0 ? ToolbarWidth : 200)) / 2;
                double tbTop = TranslationToolbarTop;
                double tbWidth = ToolbarWidth > 0 ? ToolbarWidth + 20 : 220;
                double tbHeight = ToolbarHeight > 0 ? ToolbarHeight + 10 : 55;
                var toolbarRect = new Rect(tbLeft, tbTop, tbWidth, tbHeight);

                foreach (var box in textBoxes)
                {
                    // 座標從 bitmap 空間轉換回螢幕邏輯座標
                    double scaleX = ViewportSize.Width / bitmap.Width;
                    double scaleY = ViewportSize.Height / bitmap.Height;

                    var bounds = new Rect(
                        box.Left * scaleX,
                        box.Top * scaleY,
                        box.Width * scaleX,
                        box.Height * scaleY
                    );

                    // 過濾太小的區域 + 排除工具列範圍
                    if (bounds.Width > 10 && bounds.Height > 5 && !bounds.Intersects(toolbarRect))
                    {
                        UserSelections.Add(new UserSelectionRect { Bounds = bounds });
                    }
                }
                System.Diagnostics.Debug.WriteLine($"[TranslationMode] Added {UserSelections.Count} valid selections (excluded toolbar area)");
            });

            ocrEngine.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TranslationMode] ScanAll error: {ex}");
        }
        finally
        {
            ShowTopLoadingBar = false;
            IsIndeterminate = false;
        }
    }

    /// <summary>
    /// 估算翻譯文字在特定寬度下的高度，作為 HitTest Win32 Region 大小參考
    /// </summary>
    private double EstimateTranslatedTextHeight(UserSelectionRect sel)
    {
        if (string.IsNullOrWhiteSpace(sel.TranslatedText)) return 0;

        var text = sel.TranslatedText;
        double fontSize = sel.InferredFontSize; // Use inferred font size from OCR
        double lineHeight = fontSize * 1.8;
        double padding = 28; // Border padding + extra
        double currentWidth = sel.Bounds.Width;
        double usableWidth = Math.Max(currentWidth - padding, 40);

        double EstimateTextWidth(string s)
        {
            double w = 0;
            foreach (char c in s)
            {
                if (c >= 0x2E80 && c <= 0x9FFF || c >= 0xF900 && c <= 0xFAFF || 
                    c >= 0xFF00 && c <= 0xFFEF || c >= 0x3000 && c <= 0x303F)
                    w += fontSize * 1.1; 
                else
                    w += fontSize * 0.6; 
            }
            return w;
        }

        var lines = text.Split('\n');
        int totalLines = 0;
        foreach (var line in lines)
        {
            double lineWidth = EstimateTextWidth(line);
            int wrappedLines = Math.Max(1, (int)Math.Ceiling(lineWidth / usableWidth));
            totalLines += wrappedLines;
        }

        return totalLines * lineHeight + padding;
    }

    /// <summary>
    /// V8: 根據翻譯文字長度自動撐開選取範圍
    /// </summary>
    private void AutoFitSelectionToText(UserSelectionRect sel)
    {
        if (string.IsNullOrWhiteSpace(sel.TranslatedText)) return;

        var text = sel.TranslatedText;
        double fontSize = sel.InferredFontSize; // Use inferred font size
        double lineHeight = fontSize * 1.8; // 行高
        double padding = 28; // Border padding + margin + extra

        // 估算文字寬度（考慮 CJK 全形字元）
        double EstimateTextWidth(string s)
        {
            double w = 0;
            foreach (char c in s)
            {
                if (c >= 0x2E80 && c <= 0x9FFF || c >= 0xF900 && c <= 0xFAFF || 
                    c >= 0xFF00 && c <= 0xFFEF || c >= 0x3000 && c <= 0x303F)
                    w += fontSize * 1.1; // CJK 全形
                else
                    w += fontSize * 0.6; // Latin 半形
            }
            return w;
        }

        double currentWidth = sel.Bounds.Width;
        double usableWidth = Math.Max(currentWidth - padding, 40);

        // 計算需要的行數
        var lines = text.Split('\n');
        int totalLines = 0;
        foreach (var line in lines)
        {
            double lineWidth = EstimateTextWidth(line);
            int wrappedLines = Math.Max(1, (int)Math.Ceiling(lineWidth / usableWidth));
            totalLines += wrappedLines;
        }

        double requiredHeight = totalLines * lineHeight + padding;
        double requiredWidth = currentWidth;

        // 如果文字很短（單行），確保寬度足以容納
        if (lines.Length == 1)
        {
            double singleLineWidth = EstimateTextWidth(text) + padding;
            if (singleLineWidth > currentWidth)
            {
                requiredWidth = Math.Min(singleLineWidth, 500);
            }
        }

        // 只擴展，不縮小
        double newWidth = Math.Max(currentWidth, requiredWidth);
        double newHeight = Math.Max(sel.Bounds.Height, requiredHeight);

        // 最小尺寸
        newWidth = Math.Max(newWidth, 80);
        newHeight = Math.Max(newHeight, 50);

        if (Math.Abs(newWidth - sel.Bounds.Width) > 1 || Math.Abs(newHeight - sel.Bounds.Height) > 1)
        {
            sel.Bounds = new Rect(sel.Bounds.X, sel.Bounds.Y, newWidth, newHeight);
            System.Diagnostics.Debug.WriteLine($"[AutoFit] Expanded to {newWidth:F0}x{newHeight:F0} for {totalLines} lines, text='{text}'");
        }
    }
}
