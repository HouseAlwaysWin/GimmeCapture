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
            this.RaisePropertyChanged(nameof(IsAiScanCandidateLayerVisible));
            this.RaisePropertyChanged(nameof(IsAiDetectedRectPreviewVisible));
            
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

            if (value != SnipState.Selected)
                ResetParkedToolbarIfOffScreenWhenLeavingSelection();
            
            this.RaisePropertyChanged(nameof(IsToolbarVisible));
            this.RaisePropertyChanged(nameof(IsToolbarShownOnScreen));
            UpdateMask();
        }
    }

    private bool _toolbarParkedOffscreenSaved;
    private double _savedParkToolbarLeft;
    private double _savedParkToolbarTop;
    private double _savedParkTranslationToolbarLeft;
    private double _savedParkTranslationToolbarTop;

    /// <summary>
    /// 「隱藏工具列」：移到虛擬桌面外，保留量測與 Win32 區域。翻譯模式與截圖/錄影選取後皆適用。
    /// </summary>
    public void ParkSnipToolbarOffscreen()
    {
        const double off = -50000;

        if (CurrentMode == SnipMode.Translation)
        {
            if (ToolbarLeft > -10000 && ToolbarWidth > 1 && ToolbarHeight > 1)
            {
                _savedParkToolbarLeft = ToolbarLeft;
                _savedParkToolbarTop = ToolbarTop;
                _savedParkTranslationToolbarLeft = TranslationToolbarLeft;
                _savedParkTranslationToolbarTop = TranslationToolbarTop;
                _toolbarParkedOffscreenSaved = true;
            }

            ToolbarLeft = off;
            ToolbarTop = 0;
            TranslationToolbarLeft = off;
            TranslationToolbarTop = 0;
            return;
        }

        if (CurrentState != SnipState.Selected || IsRecordingFinalizing) return;

        if (ToolbarLeft > -10000 && ToolbarWidth > 1 && ToolbarHeight > 1)
        {
            _savedParkToolbarLeft = ToolbarLeft;
            _savedParkToolbarTop = ToolbarTop;
            _toolbarParkedOffscreenSaved = true;
        }

        ToolbarLeft = off;
        ToolbarTop = 0;
    }

    public void RestoreSnipToolbarFromOffscreenPark()
    {
        if (CurrentMode == SnipMode.Translation)
        {
            if (_toolbarParkedOffscreenSaved)
            {
                ToolbarLeft = _savedParkToolbarLeft;
                ToolbarTop = _savedParkToolbarTop;
                TranslationToolbarLeft = _savedParkTranslationToolbarLeft;
                TranslationToolbarTop = _savedParkTranslationToolbarTop;
                _toolbarParkedOffscreenSaved = false;
            }
            else
                InitializeTranslationToolbarPosition();
            return;
        }

        if (CurrentState != SnipState.Selected || IsRecordingFinalizing) return;

        _toolbarParkedOffscreenSaved = false;
        UpdateToolbarPosition();
    }

    /// <summary>
    /// 離開翻譯模式時還原座標，避免截圖/錄影沿用 -50000。
    /// </summary>
    public void ResetTranslationToolbarAfterLeavingTranslationMode()
    {
        if (ToolbarLeft >= -10000) return;

        if (_toolbarParkedOffscreenSaved)
        {
            ToolbarLeft = _savedParkToolbarLeft;
            ToolbarTop = _savedParkToolbarTop;
            TranslationToolbarLeft = _savedParkTranslationToolbarLeft;
            TranslationToolbarTop = _savedParkTranslationToolbarTop;
            _toolbarParkedOffscreenSaved = false;
        }
        else
        {
            ToolbarLeft = 0;
            ToolbarTop = 20;
            TranslationToolbarLeft = -1;
            TranslationToolbarTop = 20;
        }
    }

    /// <summary>
    /// 離開選取狀態時清除螢幕外停車座標，避免影響下一次選取。
    /// </summary>
    private void ResetParkedToolbarIfOffScreenWhenLeavingSelection()
    {
        if (ToolbarLeft >= -10000) return;
        ToolbarLeft = 0;
        ToolbarTop = 0;
        _toolbarParkedOffscreenSaved = false;
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
    }

    private void StartTranslationWarmup()
    {
        if (_mainVm == null || _translationSession == null || CurrentMode != SnipMode.Translation)
        {
            return;
        }

        _translationSession.StartWarmup(_mainVm.SourceLanguage, _mainVm.TargetLanguage);
    }

    private void CancelTranslationWarmup()
    {
        _translationSession?.CancelWarmup();
    }

    private Task AwaitTranslationWarmupAsync(CancellationToken ct = default)
    {
        return _translationSession?.AwaitWarmupAsync(ct) ?? Task.CompletedTask;
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
                var mainVm = _mainVm;
                var translationSelectionMonitor = _translationSelectionMonitor;
                if (mainVm == null || translationSelectionMonitor == null || CurrentMode != SnipMode.Translation) continue;

                var updates = await translationSelectionMonitor.ProcessAsync(
                    new TranslationSelectionMonitorRequest(
                        activeSections,
                        ScreenOffset,
                        VisualScaling,
                        mainVm.SourceLanguage,
                        mainVm.TargetLanguage),
                    token);

                if (updates.Count == 0)
                {
                    continue;
                }

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ApplyTranslationSelectionUpdates(updates);
                });
                
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AutoDetect] Loop Error: {ex.Message}");
            }
        }
    }

    private void ApplyTranslationSelectionUpdates(IReadOnlyList<TranslationSelectionUpdate> updates)
    {
        bool maskChanged = false;

        foreach (var update in updates)
        {
            var selection = update.Selection;
            if (update.Kind == TranslationSelectionUpdateKind.Cleared)
            {
                selection.LastOcrText = string.Empty;
                selection.OriginalText = string.Empty;
                selection.TranslatedText = string.Empty;
                selection.IsTranslated = false;
                selection.EstimatedTextHeight = 0;
                maskChanged = true;
                continue;
            }

            selection.LastOcrText = update.OriginalText;
            selection.OriginalText = update.OriginalText;
            selection.TranslatedText = update.TranslatedText;
            selection.IsTranslated = !string.IsNullOrWhiteSpace(update.TranslatedText);
            selection.InferredFontSize = update.InferredFontSize;

            if (selection.IsTranslated)
            {
                selection.EstimatedTextHeight = EstimateTranslatedTextHeight(selection);
            }

            maskChanged = true;
        }

        if (maskChanged)
        {
            UpdateMask();
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
            // IsTranslationSelectionActive 曾固定為 true，導致這裡永遠走「有選區」分支，尚無框時仍保留整張全螢幕幾何，
            // 視覺/體感像一定要先拉框。改為依「是否已有有效選區」判斷。
            bool hasValidTranslationBox = UserSelections.AsValueEnumerable()
                .Any(s => s.Bounds.Width > 10 && s.Bounds.Height > 10);

            if (!hasValidTranslationBox)
            {
                // 尚無選區：不要鋪整張遮罩幾何（即使 MaskOpacity=0 也避免多餘層級與「待框選」感）
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

    private TranslationTool _currentTranslationTool = TranslationTool.Single;
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
        get => true;
        set { }
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
        if (IsToolbarManuallyPositioned && CurrentMode == SnipMode.Translation && ShowToolbar)
        {
            return;
        }

        if (!ShowToolbar && CurrentMode == SnipMode.Translation)
        {
            ParkSnipToolbarOffscreen();
            return;
        }

        // 根據滑鼠所在的螢幕 (ActiveScreenBounds) 置中工具列
        Rect bounds = ActiveScreenBounds.Width > 0 ? ActiveScreenBounds : new Rect(0, 0, ViewportSize.Width > 0 ? ViewportSize.Width : 1920, ViewportSize.Height > 0 ? ViewportSize.Height : 1080);

        const double margin = 20;

        // 水平置中必須用「實際工具列寬度」。若用比實際更大的寬度去算 (例如強制至少 960)，
        // Canvas.Left 會偏左，看起來像整條工具列沒有在螢幕上方置中。
        // 尚未量測時才用較大的保守值，避免第一次量測前把過窄的寬度假設拿去置中而裁切左側。
        double tw;
        if (ToolbarWidth > 0)
        {
            tw = ToolbarWidth;
        }
        else
        {
            tw = Math.Min(960, Math.Max(200, bounds.Width - margin * 2));
        }
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

        if (!ShowToolbar && CurrentState == SnipState.Selected && !IsRecordingFinalizing)
        {
            _savedParkToolbarLeft = left;
            _savedParkToolbarTop = top;
            _toolbarParkedOffscreenSaved = true;
            ToolbarLeft = -50000;
            ToolbarTop = 0;
        }
        else
        {
            ToolbarTop = top;
            ToolbarLeft = left;
        }
        
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
