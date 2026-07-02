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

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    private const int TranslationMissTolerance = 2;

    private SnipState _currentState = SnipState.Detecting;
    public SnipState CurrentState
    {
        get => _currentState;
        set
        {
            var previousState = _currentState;
            bool enteringSelected = previousState != SnipState.Selected && value == SnipState.Selected;
            System.Diagnostics.Debug.WriteLine($"[SnipState] {previousState} -> {value}");
            this.RaiseAndSetIfChanged(ref _currentState, value);
            this.RaisePropertyChanged(nameof(SelectionShadowColor));
            this.RaisePropertyChanged(nameof(IsAiScanCandidateLayerVisible));
            this.RaisePropertyChanged(nameof(IsWindowSnapCandidateLayerVisible));
            this.RaisePropertyChanged(nameof(IsHoverPreviewVisible));
            
            // If we leave Detecting state (e.g. start selecting), cancel any running scan
            _selectionStateController.HandleTransition(previousState, value);
            if (enteringSelected)
            {
                ApplyDefaultToolbarVisibilityForMode(CurrentMode);
            }
            RefreshSelectedSnapshotLock();
            this.RaisePropertyChanged(nameof(IsToolbarVisible));
            this.RaisePropertyChanged(nameof(IsToolbarShownOnScreen));
        }
    }

    private bool _toolbarParkedOffscreenSaved;
    private double _savedParkToolbarLeft;
    private double _savedParkToolbarTop;
    private double _savedParkTranslationToolbarLeft;
    private double _savedParkTranslationToolbarTop;

    /// <summary>
    /// ??橫盲??望扔??????????謜?ｇ豰謖??踐????脰??Win32 ???貔螞蹇??????????/???閰??綽??????
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

        if (_toolbarParkedOffscreenSaved && IsToolbarManuallyPositioned)
        {
            // A manually-dragged toolbar must be restored to its exact parked position: UpdateToolbarPosition()
            // defers to InitializeTranslationToolbarPosition, which refuses to re-place a manually-positioned
            // toolbar and so would leave it stuck off-screen — i.e. F4 fails to bring the toolbar back after it
            // has been moved. (The non-moved case keeps recomputing top-center as before.)
            ToolbarLeft = _savedParkToolbarLeft;
            ToolbarTop = _savedParkToolbarTop;
            _toolbarParkedOffscreenSaved = false;
        }
        else
        {
            _toolbarParkedOffscreenSaved = false;
            UpdateToolbarPosition();
        }
    }

    /// <summary>
    /// ?嚚??折??????蹇??賹慫????頦????/???撒??-50000??
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
    /// ?嚚??閰???????謒?嚗??叟□???瞍??謕??頦??嗆╰貔????蹓?謘甄?
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
                RefreshInteractionRegion();
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
                RefreshProjectedOcrRects();

                if (value
                    && EnableAIScan
                    && CurrentState == SnipState.Detecting
                    && CurrentMode != SnipMode.Translation
                    && AllScreenBounds?.Count > 0)
                {
                    TriggerAutoScanCommand?.Execute(Unit.Default).Subscribe();
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
            // Any genuine selection change (manual redraw, fullscreen, monitor pick) drops a previously-
            // picked recording window so we don't capture the wrong target. The multi-select builder sets
            // _suppressRecordHandleClear while assigning the bounding-union rect so it isn't wiped.
            if (!_suppressRecordHandleClear)
            {
                ClearRecordWindowSelection();
            }
            this.RaiseAndSetIfChanged(ref _selectionRect, value);
            RefreshInteractionRegion();
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

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsCapturing = true);
                await Task.Delay(40, token);

                IReadOnlyList<TranslationSelectionUpdate> updates;
                try
                {
                    updates = await translationSelectionMonitor.ProcessAsync(
                        new TranslationSelectionMonitorRequest(
                            activeSections,
                            ScreenOffset,
                            VisualScaling,
                            mainVm.SourceLanguage,
                            mainVm.TargetLanguage),
                        token);
                }
                finally
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsCapturing = false);
                }

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
        bool interactionRegionChanged = false;

        foreach (var update in updates)
        {
            var selection = update.Selection;
            if (update.Kind == TranslationSelectionUpdateKind.Cleared)
            {
                selection.ConsecutiveTranslationMisses++;
                if (ShouldPreservePreviousTranslation(selection))
                {
                    continue;
                }

                selection.LastOcrText = string.Empty;
                selection.OriginalText = string.Empty;
                selection.TranslatedText = string.Empty;
                selection.IsTranslated = false;
                selection.EstimatedTextHeight = 0;
                selection.DisplayFontSize = selection.InferredFontSize;
                selection.IsTextOverflowing = false;
                selection.ConsecutiveTranslationMisses = 0;
                interactionRegionChanged = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(update.TranslatedText))
            {
                selection.ConsecutiveTranslationMisses++;
                if (ShouldPreservePreviousTranslation(selection))
                {
                    continue;
                }
            }
            else
            {
                selection.ConsecutiveTranslationMisses = 0;
            }

            selection.LastOcrText = update.OriginalText;
            selection.OriginalText = update.OriginalText;
            selection.TranslatedText = update.TranslatedText;
            selection.IsTranslated = !string.IsNullOrWhiteSpace(update.TranslatedText)
                || !string.IsNullOrWhiteSpace(update.OriginalText);
            selection.InferredFontSize = update.InferredFontSize;
            selection.DisplayFontSize = update.InferredFontSize;
            selection.IsTextOverflowing = false;

            if (selection.IsTranslated)
            {
                AutoFitSelectionToText(selection);
            }

            interactionRegionChanged = true;
        }

        if (interactionRegionChanged)
        {
            RefreshInteractionRegion();
        }
    }

    private static bool ShouldPreservePreviousTranslation(UserSelectionRect selection)
    {
        return selection.ConsecutiveTranslationMisses < TranslationMissTolerance
            && selection.IsTranslated
            && !string.IsNullOrWhiteSpace(selection.TranslatedText);
    }

    /* Speech helper methods removed */

    private int _interactionRegionRevision;
    public int InteractionRegionRevision
    {
        get => _interactionRegionRevision;
        private set => this.RaiseAndSetIfChanged(ref _interactionRegionRevision, value);
    }

    public void RefreshInteractionRegion()
    {
        // Bump the revision synchronously so its value is correct immediately — callers (and unit tests) don't
        // have to wait on the dispatcher. Only the change notification is marshalled to the UI thread, because
        // the observer recomputes the native interaction region and must run there. (Previously the whole
        // method deferred via Post, so off the UI thread — e.g. in tests with no message loop — the bump was
        // dropped, making SelectionChanges_RefreshInteractionRegionWithoutScreenMask flaky.)
        _interactionRegionRevision++;
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            this.RaisePropertyChanged(nameof(InteractionRegionRevision));
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(InteractionRegionRevision)));
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
        set
        {
            this.RaiseAndSetIfChanged(ref _screenOffset, value);
            InvalidateTranslationOcrSearchCache();
        }
    }

    private double _visualScaling = 1.0;
    public double VisualScaling
    {
        get => _visualScaling;
        set
        {
            this.RaiseAndSetIfChanged(ref _visualScaling, value);
            InvalidateTranslationOcrSearchCache();
        }
    }

    private Size _viewportSize;
    public Size ViewportSize
    {
        get => _viewportSize;
        set 
        {
            this.RaiseAndSetIfChanged(ref _viewportSize, value);
            InvalidateTranslationOcrSearchCache();
            RefreshInteractionRegion();
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
        set
        {
            if (Math.Abs(_toolbarWidth - value) < 0.5)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _toolbarWidth, value);
            // Re-center the fixed top-center toolbar (all modes) when its measured width changes.
            if (!IsToolbarManuallyPositioned)
            {
                PositionFixedTopCenterToolbar();
            }
        }
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

    public double ToolbarMaxWidth
    {
        get
        {
            // Translation toolbar should wrap within the active monitor, not the full virtual desktop.
            if (CurrentMode == SnipMode.Translation && ActiveScreenBounds.Width > 100)
            {
                return Math.Max(200, ActiveScreenBounds.Width - 40);
            }

            return ViewportSize.Width > 100 ? ViewportSize.Width - 40 : 2000;
        }
    }

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
            RefreshInteractionRegion();
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

    // ?折???鈭??謅??菜捕?????????澈?嚗??????謘??
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
        // ????輯撒???舀??秋???∪????????祗?????插???????鞈????秋撒?仿?潸??
        // ?選?謆?????湔??????????????∪????????∟??
        // All selection modes (Translation / Recording / Screenshot) use the fixed top-center toolbar.

        if (IsToolbarManuallyPositioned && ShowToolbar)
        {
            return;
        }

        if (!ShowToolbar)
        {
            if (CurrentMode == SnipMode.Translation)
            {
                ParkSnipToolbarOffscreen();
            }
            else
            {
                if (ToolbarLeft > -10000 && ToolbarWidth > 1 && ToolbarHeight > 1)
                {
                    _savedParkToolbarLeft = ToolbarLeft;
                    _savedParkToolbarTop = ToolbarTop;
                    _toolbarParkedOffscreenSaved = true;
                }

                ToolbarLeft = -50000;
                ToolbarTop = 0;
            }
            return;
        }

        // ?撖?????????嚗? (ActiveScreenBounds) ?菜???鈭???
        Rect bounds = ActiveScreenBounds.Width > 0 ? ActiveScreenBounds : new Rect(0, 0, ViewportSize.Width > 0 ? ViewportSize.Width : 1920, ViewportSize.Height > 0 ? ViewportSize.Height : 1080);

        const double margin = 20;

        double tw = ToolbarWidth > 1
            ? ToolbarWidth
            : Math.Min(1080, Math.Max(200, bounds.Width - margin * 2));

        double left = bounds.X + (bounds.Width - tw) / 2;
        if (tw >= bounds.Width - margin * 2)
        {
            left = bounds.X + margin;
        }
        else
        {
            left = Math.Clamp(left, bounds.X + margin, bounds.X + bounds.Width - tw - margin);
        }

        TranslationToolbarLeft = left;
        TranslationToolbarTop = bounds.Y + margin;

        ToolbarLeft = TranslationToolbarLeft;
        ToolbarTop = TranslationToolbarTop;
    }

    /// <summary>Pins the toolbar to the top-center of the active screen (Translation and Recording modes).</summary>
    private void PositionFixedTopCenterToolbar() => InitializeTranslationToolbarPosition();

    private void UpdateToolbarPosition()
    {
        // ?折???????璆????選????InitializeTranslationToolbarPosition ???
        if (CurrentMode == SnipMode.Translation) return;

        // Recording and Screenshot both keep a fixed top-center toolbar (does not follow the selection).
        PositionFixedTopCenterToolbar();
        this.RaisePropertyChanged(nameof(ToolbarMaxWidth));
    }
}
