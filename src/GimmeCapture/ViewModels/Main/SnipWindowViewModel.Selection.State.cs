using Avalonia;
using Avalonia.Media;
using GimmeCapture.Models;
using ReactiveUI;
using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Media;
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
                // Restart scan if enabled (only after AllScreenBounds is populated)
                // 翻譯模式不使用 SAM2 掃描
                if (ShowAIScanBox && CurrentMode != SnipMode.Translation && AllScreenBounds?.Count > 0)
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
        set 
        {
            if (value != _showTopLoadingBar)
            {
                this.RaiseAndSetIfChanged(ref _showTopLoadingBar, value);
                UpdateMask();
            }
        }
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
                    if (CurrentState == SnipState.Detecting && CurrentMode != SnipMode.Translation && AllScreenBounds?.Count > 0)
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

    private bool _isTranslating;
    public bool IsTranslating
    {
        get => _isTranslating;
        set
        {
            System.Diagnostics.Debug.WriteLine($"[Translation] IsTranslating -> {value}");
            this.RaiseAndSetIfChanged(ref _isTranslating, value);
        }
    }

    public ObservableCollection<TranslatedBlock> TranslatedBlocks { get; } = new();
    public ObservableCollection<UserSelectionRect> UserSelections { get; } = new();
    private TranslationService? _translationService;
    private CancellationTokenSource? _translationCts;
    private CancellationTokenSource? _translationWarmupCts;
    private Task? _translationWarmupTask;


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

    private void StartTranslationWarmup()
    {
        if (_mainVm == null || CurrentMode != SnipMode.Translation)
        {
            return;
        }

        if (_translationWarmupTask is { IsCompleted: false })
        {
            return;
        }

        _translationWarmupCts?.Cancel();
        _translationWarmupCts?.Dispose();
        _translationWarmupCts = new CancellationTokenSource();
        var token = _translationWarmupCts.Token;

        _translationWarmupTask = Task.Run(async () =>
        {
            try
            {
                _translationService ??= new TranslationService(_mainVm.AIResourceService, _mainVm.AppSettingsService, _mainVm.MarianMTService);

                // Keep language settings in sync before warm-up.
                _mainVm.AppSettingsService.Settings.TargetLanguage = _mainVm.TargetLanguage;
                _mainVm.AppSettingsService.Settings.SourceLanguage = _mainVm.SourceLanguage;

                await _translationService.WarmUpAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Expected when leaving translation mode.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TranslationWarmup] Failed: {ex.Message}");
            }
        }, token);
    }

    private void CancelTranslationWarmup()
    {
        _translationWarmupCts?.Cancel();
        _translationWarmupCts?.Dispose();
        _translationWarmupCts = null;
    }

    private async Task AwaitTranslationWarmupAsync(CancellationToken ct = default)
    {
        var warmupTask = _translationWarmupTask;
        if (warmupTask == null)
        {
            return;
        }

        try
        {
            await warmupTask.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TranslationWarmup] Await failed: {ex.Message}");
        }
    }

    private async Task AutoDetectLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500, token);

                var activeSections = UserSelections.AsValueEnumerable().Where(s => s.IsAutoDetectEnabled).ToList();
                if (activeSections.Count == 0) continue;
                if (_mainVm == null || CurrentMode != SnipMode.Translation) continue;

                bool hasVisualSections = activeSections.AsValueEnumerable().Any();
                if (hasVisualSections)
                {
                    bool isOcrReady = await _mainVm.AIResourceService.EnsureOCRAsync();
                    if (!isOcrReady) continue;

                    if (_sharedOcrEngine == null)
                    {
                        _sharedOcrEngine = new GimmeCapture.Services.OCR.PaddleOCREngine(_mainVm.AIResourceService, _mainVm.AppSettingsService);
                    }
                }

                var ocrLang = _mainVm.AppSettingsService.Settings.SourceLanguage;
                // Auto detect script if source is auto
                if (ocrLang == OCRLanguage.Auto)
                {
                    // Fallback to Trad Chinese or Japanese based on some heuristic or just default to Trad Chinese for now
                    // For better auto-detect, we might need a fast script detector
                    ocrLang = OCRLanguage.TraditionalChinese;
                }

                if (hasVisualSections && _sharedOcrEngine != null)
                {
                    await _sharedOcrEngine.EnsureLoadedAsync(ocrLang, token);
                }

                foreach (var sel in activeSections)
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        /* Audio panel logic removed */

                        var rect = sel.Bounds;
                        if (rect.Width <= 10 || rect.Height <= 10) continue;

                        // Capture screen without flickering (UI will be hidden via WDA_EXCLUDEFROMCAPTURE in Translate mode)
                        using var bitmap = await _captureService.CaptureScreenAsync(rect, ScreenOffset, VisualScaling, false);
                        if (bitmap == null) continue;

                        // Calculate Pixel Diff
                        var currentPixels = bitmap.Bytes;
                        int width = bitmap.Width;
                        int height = bitmap.Height;
                        bool hasSignificantChange = true; // Default to true for first run

                        if (sel.LastPixels != null && sel.LastPixelWidth == width && sel.LastPixelHeight == height && currentPixels.Length == sel.LastPixels.Length)
                        {
                            int diffCount = 0;
                            int totalPixels = width * height;
                            int step = 8; // Sample every 8th pixel for better performance in large regions

                            for (int i = 0; i < currentPixels.Length; i += step * 4) 
                            {
                                // SAD (Sum of Absolute Differences) on RGB
                                int rDiff = currentPixels[i] - sel.LastPixels[i];
                                int gDiff = currentPixels[i + 1] - sel.LastPixels[i + 1];
                                int bDiff = currentPixels[i + 2] - sel.LastPixels[i + 2];
                                
                                // Square sum for better sensitivity to noise vs content change? 
                                // No, SAD is fine but let's use a lower per-pixel noise threshold (30 instead of 50)
                                // to catch subtle subtitle changes while ignoring compression artifacts.
                                int sad = Math.Abs(rDiff) + Math.Abs(gDiff) + Math.Abs(bDiff);

                                if (sad > 45) // Total difference threshold (sums to ~15 per channel)
                                {
                                    diffCount++;
                                }
                            }

                            // Calculate percentage based on checked pixels
                            double diffPercentage = (double)diffCount / (totalPixels / step);
                            
                            // 5% change threshold as requested
                            if (diffPercentage < 0.05) 
                            {
                                hasSignificantChange = false;
                            }
                        }

                        // Update cached pixels
                        sel.LastPixels = currentPixels;
                        sel.LastPixelWidth = width;
                        sel.LastPixelHeight = height;

                        if (!hasSignificantChange)
                        {
                            continue; // Skip OCR completely if the visual didn't change enough
                        }

                        System.Diagnostics.Debug.WriteLine($"[AutoDetect] Significant visual change detected. Running OCR & Translation...");

                        // Ensure TranslationService is initialized
                        if (_translationService == null && _mainVm != null)
                        {
                            _translationService = new TranslationService(_mainVm.AIResourceService, _mainVm.AppSettingsService, _mainVm.MarianMTService);
                        }

                        // Sync language settings before translation (must match manual translate path)
                        if (_mainVm != null)
                        {
                            _mainVm.AppSettingsService.Settings.TargetLanguage = _mainVm.TargetLanguage;
                            _mainVm.AppSettingsService.Settings.SourceLanguage = _mainVm.SourceLanguage;
                            System.Diagnostics.Debug.WriteLine($"[AutoDetect] Language sync: Source={_mainVm.SourceLanguage}, Target={_mainVm.TargetLanguage}");
                        }

                        // Ask TranslationService to translate just this text
                        if (_translationService != null)
                        {
                            // We don't want to freeze the UI or show loading bars for background updates
                            // AnalyzeAndTranslateAsync will handle OCR + LLM
                            var (blocks, errorKey) = await _translationService.AnalyzeAndTranslateAsync(bitmap, VisualScaling, token);
                            if (blocks == null || blocks.Count == 0)
                            {
                                // Continuous detect mode: if no text is detected this round, clear stale text from previous round.
                                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    if (sel.IsTranslated || !string.IsNullOrWhiteSpace(sel.TranslatedText) || !string.IsNullOrWhiteSpace(sel.OriginalText))
                                    {
                                        sel.LastOcrText = string.Empty;
                                        sel.OriginalText = string.Empty;
                                        sel.TranslatedText = string.Empty;
                                        sel.IsTranslated = false;
                                        sel.EstimatedTextHeight = 0;
                                        UpdateMask();
                                    }
                                });
                                continue;
                            }

                            var combinedText = string.Join("\n", blocks.AsValueEnumerable().Select(b => b.TranslatedText).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray());
                            var combinedOriginalText = string.Join("\n", blocks.AsValueEnumerable().Select(b => b.OriginalText).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray());
                            
                            // Prevent re-updating if text hasn't logically changed despite visual noise
                            if (combinedText == sel.TranslatedText) continue;
                            
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                sel.LastOcrText = string.Join("\n", blocks.AsValueEnumerable().Select(b => b.OriginalText).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray()); // Update tracking hash
                                sel.OriginalText = combinedOriginalText;
                                sel.TranslatedText = combinedText;
                                sel.IsTranslated = !string.IsNullOrWhiteSpace(combinedText);
                                
                                // Propagate inferred font size from blocks
                                if (blocks.AsValueEnumerable().Any())
                                {
                                    sel.InferredFontSize = blocks[0].InferredFontSize;
                                }

                                if (sel.IsTranslated)
                                {
                                    sel.EstimatedTextHeight = EstimateTranslatedTextHeight(sel);
                                }
                                UpdateMask();
                            });
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

    /* Speech helper methods removed */

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
        if (SelectionRect.Width > 0 && SelectionRect.Height > 0 && CurrentMode != SnipMode.Translation)
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
        if (CurrentMode == SnipMode.Translation)
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
        get => CurrentMode == SnipMode.Translation ? 0.0 : _maskOpacity;
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
        set
        {
            this.RaiseAndSetIfChanged(ref _selectionBorderThickness, value);
            this.RaisePropertyChanged(nameof(SelectionBorderOuterMargin));
        }
    }

    // Draw border/decorations outside the selected content bounds,
    // so recording of SelectionRect does not include the border line.
    public Thickness SelectionBorderOuterMargin => new(-SelectionBorderThickness, -SelectionBorderThickness, -SelectionBorderThickness, -SelectionBorderThickness);

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

    private TranslationTool _currentTranslationTool = TranslationTool.Cursor;
    public TranslationTool CurrentTranslationTool
    {
        get => _currentTranslationTool;
        set 
        {
            this.RaiseAndSetIfChanged(ref _currentTranslationTool, value);
            this.RaisePropertyChanged(nameof(IsTranslationSelectionActive));
            this.RaisePropertyChanged(nameof(IsTranslationCursorMode));
            this.RaisePropertyChanged(nameof(IsTranslationSingleMode));
            this.RaisePropertyChanged(nameof(IsTranslationMultiMode));
            this.RaisePropertyChanged(nameof(TranslateAllHotkey));
            this.RaisePropertyChanged(nameof(ScanAllHotkey));
            this.RaisePropertyChanged(nameof(ToggleSelectHotkey));
            this.RaisePropertyChanged(nameof(TranslateAllTooltip));
            this.RaisePropertyChanged(nameof(ScanAllTooltip));
            this.RaisePropertyChanged(nameof(ToggleSelectTooltip));
            UpdateMask();
        }
    }

    public bool IsTranslationSelectionActive
    {
        get => CurrentTranslationTool == TranslationTool.Single || CurrentTranslationTool == TranslationTool.Multi;
        set 
        {
            if (value && CurrentTranslationTool == TranslationTool.Cursor)
                CurrentTranslationTool = TranslationTool.Single;
            else if (!value)
                CurrentTranslationTool = TranslationTool.Cursor;
        }
    }

    public bool IsTranslationCursorMode
    {
        get => CurrentTranslationTool == TranslationTool.Cursor;
        set { if (value) CurrentTranslationTool = TranslationTool.Cursor; }
    }

    public bool IsTranslationSingleMode
    {
        get => CurrentTranslationTool == TranslationTool.Single;
        set { if (value) CurrentTranslationTool = TranslationTool.Single; }
    }

    public bool IsTranslationMultiMode
    {
        get => CurrentTranslationTool == TranslationTool.Multi;
        set { if (value) CurrentTranslationTool = TranslationTool.Multi; }
    }

    /* Audio mode property removed */

    private void EnsureAudioTranslationBox()
    {
        if (UserSelections.AsValueEnumerable().Any(x => x.IsAudioPanel))
        {
            return;
        }

        // Place an initial subtitle-like box near the lower third of the active screen.
        var bounds = ActiveScreenBounds.Width > 0
            ? ActiveScreenBounds
            : new Rect(0, 0, ViewportSize.Width > 0 ? ViewportSize.Width : 1920, ViewportSize.Height > 0 ? ViewportSize.Height : 1080);

        double width = Math.Clamp(bounds.Width * 0.62, 360, 960);
        double height = Math.Clamp(bounds.Height * 0.18, 90, 260);
        double x = bounds.X + (bounds.Width - width) / 2.0;
        double y = bounds.Y + bounds.Height * 0.72 - (height / 2.0);

        UserSelections.Add(new UserSelectionRect
        {
            Bounds = new Rect(x, y, width, height),
            IsTranslated = false,
            IsAudioPanel = true,
            IsAutoDetectEnabled = true,
            InferredFontSize = 18
        });
    }

    private void CloseAudioTranslationBoxes()
    {
        var audioPanels = UserSelections.AsValueEnumerable().Where(x => x.IsAudioPanel).ToList();
        foreach (var item in audioPanels)
        {
            UserSelections.Remove(item);
        }
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
        if (IsToolbarManuallyPositioned && CurrentMode == SnipMode.Translation)
        {
            return;
        }

        // 根據滑鼠所在的螢幕 (ActiveScreenBounds) 置中工具列
        Rect bounds = ActiveScreenBounds.Width > 0 ? ActiveScreenBounds : new Rect(0, 0, ViewportSize.Width > 0 ? ViewportSize.Width : 1920, ViewportSize.Height > 0 ? ViewportSize.Height : 1080);

        // 翻譯列（模式鈕 + 游標/單選/多選 + 語言 Combo 等）實際寬度遠大於截圖/錄影列。
        // 若仍用偏小的 ToolbarWidth（剛切換模式尚未量測、或預設 200）去做水平置中，
        // Canvas.Left 會依「過窄的寬度」置中，實際控制項較寬，左側會被裁到視窗外，
        // 看起來像「語言列右邊還在、左邊整段不見」。
        const double translationMinCenteringWidth = 960;
        double tw = ToolbarWidth > 0 ? ToolbarWidth : 200;
        if (CurrentMode == SnipMode.Translation)
        {
            tw = Math.Max(tw, translationMinCenteringWidth);
        }

        double margin = 20;
        double maxTw = Math.Max(0, bounds.Width - margin * 2);
        tw = Math.Min(tw, maxTw);

        double left = bounds.X + (bounds.Width - tw) / 2;
        double minLeft = bounds.X;
        double maxLeft = bounds.X + bounds.Width - tw;
        if (maxLeft < minLeft)
        {
            left = minLeft;
        }
        else
        {
            left = Math.Clamp(left, minLeft, maxLeft);
        }

        TranslationToolbarLeft = left;
        TranslationToolbarTop = bounds.Y + 20;

        // 同步更新 XAML 綁定的工具列位置
        ToolbarLeft = TranslationToolbarLeft;
        ToolbarTop = TranslationToolbarTop;
    }

    private void UpdateToolbarPosition()
    {
        // 翻譯模式下工具列位置由 InitializeTranslationToolbarPosition 管理
        if (CurrentMode == SnipMode.Translation) return;

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
        var targetMonitor = AllScreenBounds?.AsValueEnumerable().FirstOrDefault(s => 
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
}
