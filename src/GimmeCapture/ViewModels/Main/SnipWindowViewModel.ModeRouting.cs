using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using ReactiveUI;
using System;
using System.Threading.Tasks;
using System.Reactive;
using System.Reactive.Linq;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Shared;
using GimmeCapture.Services.Platforms.Avalonia;

namespace GimmeCapture.ViewModels.Main;

internal enum RecordingPinAction
{
    None,
    StartRecording,
    PinRecording
}

public partial class SnipWindowViewModel
{
    private static readonly string[] _modeStatePropertyNames =
    {
        nameof(IsRecordingMode),
        nameof(IsTranslationMode),
        nameof(IsScreenshotMode),
        nameof(HideFrameBorder),
        nameof(HideSelectionDecoration),
        nameof(ModeDisplayName),
        nameof(IsToolbarVisible),
        nameof(IsToolbarShownOnScreen),
        nameof(IsAiScanCandidateLayerVisible),
        nameof(IsWindowSnapCandidateLayerVisible),
        nameof(IsHoverPreviewVisible)
    };

    private static readonly string[] _modeHotkeyPropertyNames =
    {
        nameof(CopyHotkey),
        nameof(UndoHotkey),
        nameof(RedoHotkey),
        nameof(ClearHotkey),
        nameof(SaveHotkey),
        nameof(CloseHotkey),
        nameof(RectangleHotkey),
        nameof(EllipseHotkey),
        nameof(ArrowHotkey),
        nameof(LineHotkey),
        nameof(PenHotkey),
        nameof(TextHotkey),
        nameof(MosaicHotkey),
        nameof(BlurHotkey),
        nameof(FullscreenSelectHotkey),
        nameof(ActiveActionHotkey),
        nameof(ActiveToolbarHotkey),
        nameof(TranslateAllHotkey),
        nameof(TranslatePinHotkey),
        nameof(ScanAllHotkey),
        nameof(ClearAllHotkey),
        nameof(ToggleSelectHotkey),
        nameof(AutoDetectHotkey),
        nameof(SwitchToSnipHotkey),
        nameof(SwitchToRecordHotkey),
        nameof(SwitchToTranslateHotkey)
    };

    private static readonly string[] _modeTooltipPropertyNames =
    {
        nameof(UndoTooltip),
        nameof(RedoTooltip),
        nameof(ClearTooltip),
        nameof(SaveTooltip),
        nameof(CopyTooltip),
        nameof(PinTooltip),
        nameof(ScrollingCaptureTooltip),
        nameof(RectangleTooltip),
        nameof(EllipseTooltip),
        nameof(ArrowTooltip),
        nameof(LineTooltip),
        nameof(PenTooltip),
        nameof(TextTooltip),
        nameof(CalloutTooltip),
        nameof(MosaicTooltip),
        nameof(BlurTooltip),
        nameof(HighlighterTooltip),
        nameof(StepTooltip),
        nameof(BringToFrontTooltip),
        nameof(SendToBackTooltip),
        nameof(FullscreenSelectTooltip),
        nameof(SnipTooltip),
        nameof(RecordTooltip),
        nameof(TranslateTooltip),
        nameof(HideTranslationResultsTooltip),
        nameof(TranslationPinTooltip),
        nameof(TranslateAllTooltip),
        nameof(ScanAllTooltip),
        nameof(ClearAllTooltip),
        nameof(ToggleSelectTooltip),
        nameof(AutoDetectTooltip),
        nameof(ToggleToolbarTooltip)
    };

    public bool IsRecordingMode => CurrentMode == SnipMode.Recording;
    public bool IsTranslationMode => CurrentMode == SnipMode.Translation;
    public bool IsScreenshotMode => CurrentMode == SnipMode.Screenshot;


    private void SetCurrentMode(SnipMode value)
    {
        var oldMode = _currentMode;
        if (oldMode == value) return;

        DeactivateDrawingInteraction();
        this.RaiseAndSetIfChanged(ref _currentMode, value, nameof(CurrentMode));
        if (CurrentState == SnipState.Selected)
        {
            ApplyDefaultToolbarVisibilityForMode(value);
        }
        RefreshSelectedSnapshotLock();
        SyncAudioMeterTimerWithMode();

        // Logic from old IsRecordingMode / IsTranslationMode setters
        if (value == SnipMode.Translation)
        {
            // 截圖/錄影的 AI 自動選取與視窗候選框不適用於翻譯模式
            ClearAiScanOverlayState();
            ExitTranslationOcrSearch();

            SelectionRect = new Rect(0, 0, 0, 0);
            StartAutoDetectLoop();
            LogTranslationMemoryState("translation-mode-enter");
        }
        else if (oldMode == SnipMode.Translation)
        {
            PersistTranslationSelectionsAction?.Invoke();
            ExitTranslationOcrSearch();
            InvalidateTranslationOcrSearchCache();
            ResetTranslationToolbarAfterLeavingTranslationMode();
            // 清除多重選取
            UserSelections.Clear();
            // 關閉自動偵測
            IsGlobalAutoDetectEnabled = false;
            
            StopAutoDetectLoop();
            CancelTranslationWarmup();
            ReleaseTranslationHeavyResources(trimProcessWorkingSet: true, phase: "translation-mode-exit");
        }

        // Common updates
        SelectionBorderColor = _mainVm?.BorderColor ?? Colors.Yellow;
        if (value == SnipMode.Screenshot || value == SnipMode.Recording)
        {
            LogTranslationMemoryState(value == SnipMode.Screenshot ? "snip-mode-enter" : "record-mode-enter");
        }

        // Notify all related properties
        RaiseProperties(_modeStatePropertyNames);

        // Notify hotkeys and tooltips
        RaiseProperties(_modeHotkeyPropertyNames);
        RaiseProperties(_modeTooltipPropertyNames);

        _selectionStateController.HandleModeTransition(oldMode, value, CurrentState);
        TranslationResultLayerManager.RefreshWindowState();

        // Recording AND Screenshot both keep the toolbar fixed top-center (like Translation) so it's reachable
        // immediately on entry — before drawing a selection — instead of only appearing after a box is dragged.
        if (value == SnipMode.Recording || value == SnipMode.Screenshot)
        {
            PositionFixedTopCenterToolbar();
        }
    }
    
    private bool _isGlobalAutoDetectEnabled;
    public bool IsGlobalAutoDetectEnabled
    {
        get => _isGlobalAutoDetectEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _isGlobalAutoDetectEnabled, value);
            // 同步更新所有當前翻譯區塊的偵測狀態
            foreach (var sel in UserSelections)
            {
                if (!sel.IsAudioPanel)
                {
                    sel.IsAutoDetectEnabled = value;
                }
            }
            // 如果開啟，喚醒背景迴圈，或者直接依靠現有的 Loop
            if (value && CurrentMode == SnipMode.Translation)
            {
                StartAutoDetectLoop();
            }
        }
    }

    /// <summary>
    /// SnipToolbar 是否留在視覺樹中。翻譯模式或截圖/錄影已選取時固定為 true，以便「隱藏」時停到螢幕外仍能量測寬高。
    /// </summary>
    // The toolbar is shown immediately on entry in every mode — Translation, Recording AND Snip — including
    // before any selection exists AND during the OCR candidate scan (Detecting), so the user never has to drag a
    // box first to reveal it. Only hidden while a recording is finalizing. (The OCR candidate click is kept
    // working during Detecting by skipping the press-handler's coordinate toolbar-guard there — see
    // SnipWindow.Pointer.cs OnPointerPressed — not by hiding the toolbar.)
    public bool IsToolbarVisible =>
        !IsRecordingFinalizing
        && (CurrentMode == SnipMode.Translation
            || CurrentMode == SnipMode.Recording
            || CurrentMode == SnipMode.Screenshot);

    /// <summary>
    /// 使用者是否「看得到」工具列（未停到螢幕外）；Win32 命中與 Canvas 互動以此為準。
    /// </summary>
    public bool IsToolbarShownOnScreen =>
        ShowToolbar && !IsRecordingFinalizing
        && (CurrentMode == SnipMode.Translation
            || CurrentMode == SnipMode.Recording
            || CurrentMode == SnipMode.Screenshot);

    public string ModeDisplayName => CurrentMode switch
    {
        SnipMode.Translation => LocalizationService.Instance["CaptureModeTranslation"] ?? "Translation",
        SnipMode.Recording => LocalizationService.Instance["CaptureModeRecord"],
        _ => LocalizationService.Instance["CaptureModeNormal"]
    };

    public bool IsRecordingActive => _recordingService?.State == RecordingState.Recording;

    // Current recording format (gif, mp4, webm, etc.)
    public string RecordFormat => _mainVm?.RecordingSettings.RecordFormat ?? "mp4";

    private TimeSpan _recordingDuration = TimeSpan.Zero;
    public TimeSpan RecordingDuration
    {
        get => _recordingDuration;
        set 
        {
            this.RaiseAndSetIfChanged(ref _recordingDuration, value);
            this.RaisePropertyChanged(nameof(RecordingDurationText));
        }
    }

    public string RecordingDurationText => RecordingDuration.ToString(@"mm\:ss");

    private Avalonia.Threading.DispatcherTimer? _recordTimer;
    private DateTime _recordingActiveStartUtc;
    private TimeSpan _recordingAccumulatedDuration = TimeSpan.Zero;
    private RecordingState _lastRecordingState = RecordingState.Idle;

    private bool _isRecordingFinalizing;
    public bool IsRecordingFinalizing
    {
        get => _isRecordingFinalizing;
        set
        {
            this.RaiseAndSetIfChanged(ref _isRecordingFinalizing, value);
            this.RaisePropertyChanged(nameof(IsToolbarVisible));
            this.RaisePropertyChanged(nameof(IsToolbarShownOnScreen));
            RaiseProperties(nameof(HideFrameBorder), nameof(HideSelectionDecoration));
        }
    }

    // Action Helpers
    public bool HideSelectionDecoration
    {
        get
        {
            // "Hide selection decoration (record)" applies DURING recording only: when checked, the chrome is
            // hidden while recording / paused / finalizing; while still selecting the region it is always shown
            // so the user can see what will be captured. Snip mode keeps its own setting (applied to selection).
            if (CurrentMode == SnipMode.Recording)
            {
                bool recordingActive = IsRecordingFinalizing || RecState == RecordingState.Recording || RecState == RecordingState.Paused;
                return recordingActive && (_mainVm?.HideRecordSelectionDecoration ?? false);
            }
            return _mainVm?.HideSnipSelectionDecoration ?? false;
        }
    }

    public bool HideFrameBorder
    {
        get
        {
            // "Hide selection border (record)" applies DURING recording only (see HideSelectionDecoration):
            // when checked, the border is hidden while recording / paused / finalizing; while selecting the
            // region it is always shown. Snip mode keeps its own setting (applied to selection).
            if (CurrentMode == SnipMode.Recording)
            {
                bool recordingActive = IsRecordingFinalizing || RecState == RecordingState.Recording || RecState == RecordingState.Paused;
                return recordingActive && (_mainVm?.HideRecordSelectionBorder ?? false);
            }
            return _mainVm?.HideSnipSelectionBorder ?? false;
        }
    }

    private SnipAutoAction _autoActionMode = SnipAutoAction.None;
    public SnipAutoAction AutoActionMode
    {
        get => _autoActionMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoActionMode, value);
            if (value != SnipAutoAction.None && CurrentState == SnipState.Selected)
            {
                TriggerAutoAction();
            }
        }
    }

    private void TriggerAutoAction()
    {
        if (AutoActionMode == SnipAutoAction.Copy)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Copy().Forget("SnipAutoAction.Copy"));
        }
        else if (AutoActionMode == SnipAutoAction.Pin)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Pin().Forget("SnipAutoAction.Pin"));
        }
        else if (AutoActionMode == SnipAutoAction.EnterRecordMode)
        {
             if (CurrentMode != SnipMode.Recording) CurrentMode = SnipMode.Recording;
             // USER REQUEST: Selection only, record manually or via the record action hotkey.
        }
        else if (AutoActionMode == SnipAutoAction.TextCopy)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => ExecuteTextCopyAsync().Forget("QuickOcr.ExecuteTextCopy"));
        }
        else if (AutoActionMode == SnipAutoAction.ScrollingCapture)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ExecuteScrollingCapture().Forget("SnipAutoAction.ScrollingCapture"));
        }
    }

    public RecordingState RecState => _recordingService?.State ?? RecordingState.Idle;

    private string? _currentRecordingPath;
    private DateTime _lastActiveActionHotkeyUtc = DateTime.MinValue;

    // Commands (Partial declarations not needed if initialized in constructor)
    // But we need to define the properties here to be grouped

    public ReactiveCommand<Unit, Unit> CopyCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> PinCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ScrollingCaptureCommand { get; set; } = null!;
    public ReactiveCommand<ScrollingCaptureDirection, Unit> SetScrollDirectionCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> CloseCommand { get; set; } = null!;
    // Esc-specific: two-stage dismiss (clear a drawn box first, then close). Kept separate from CloseCommand
    // so the right-click Close menu and after-capture closes still close outright.
    public ReactiveCommand<Unit, Unit> DismissOrCloseCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleModeCommand { get; set; } = null!;
    public ReactiveCommand<bool, Unit> SetCaptureModeCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SetTranslationModeCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> StartRecordingCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> PauseRecordingCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> StopRecordingCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> CopyRecordingCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> HandleScreenshotModeHotkeyCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> HandleRecordingModeHotkeyCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleSelectionModeCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> HandleActiveActionHotkeyCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SwitchToSnipCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SwitchToRecordCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SwitchToTranslateCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> RemoveBackgroundCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> InteractiveRemovalCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleTopmostCommand { get; set; } = null!;
    private DateTime _lastGlobalHotkeyUtc = DateTime.MinValue;
    private int _lastGlobalHotkeyId = -1;

    public void HandleGlobalHotkey(int id)
    {
        if (_mainVm == null) return;

        // While a manual scrolling-capture session is active: pressing the trigger (Shift+F5)
        // again finishes it, and the temporary global hotkeys registered for the session finish
        // (Pin key) or cancel (Close key). These work even when the target window is focused,
        // unlike the low-level keyboard hook.
        if (_manualScrollActive && (id == HotkeyIds.ScrollingCapture || id == HotkeyIds.ScrollingCaptureFinish))
        {
            FinishManualScrollCapture(cancelled: false);
            return;
        }

        if (_manualScrollActive && id == HotkeyIds.ScrollingCaptureCancel)
        {
            FinishManualScrollCapture(cancelled: true);
            return;
        }

        var now = DateTime.UtcNow;
        if (id == _lastGlobalHotkeyId && (now - _lastGlobalHotkeyUtc) < TimeSpan.FromMilliseconds(600))
        {
            return;
        }
        _lastGlobalHotkeyId = id;
        _lastGlobalHotkeyUtc = now;
        
        string pressedHotkey = _mainVm.HotkeyRouterService.GetPressedHotkeyText(
            id,
            SnipHotkey,
            RecordHotkey,
            TranslateHotkey,
            TextCopyHotkey);

        System.Diagnostics.Debug.WriteLine($"[SnipWindowViewModel] HandleGlobalHotkey ID={id}, Pressed={pressedHotkey}, ActiveAction={ActiveActionHotkey}, ActiveToolbar={ActiveToolbarHotkey}, Mode={CurrentMode}");

        if (TryHandleModeSwitchHotkey(pressedHotkey)) return;

        var routeAction = _mainVm.HotkeyRouterService.ResolveSnipGlobalHotkeyAction(
            id,
            pressedHotkey,
            ActiveActionHotkey,
            ActiveToolbarHotkey);

        switch (routeAction)
        {
            case HotkeyRouterService.SnipGlobalHotkeyAction.ActiveAction:
                System.Diagnostics.Debug.WriteLine("[SnipWindowViewModel] Routed to ActiveAction hotkey.");
                HandleActiveActionHotkeyCommand?.Execute().Subscribe();
                break;
            case HotkeyRouterService.SnipGlobalHotkeyAction.ToggleToolbar:
                System.Diagnostics.Debug.WriteLine("[SnipWindowViewModel] Routed to ToggleToolbar hotkey.");
                ToggleToolbarCommand?.Execute().Subscribe();
                break;
            case HotkeyRouterService.SnipGlobalHotkeyAction.ScreenshotMode:
                HandleScreenshotModeHotkeyCommand?.Execute().Subscribe();
                break;
            case HotkeyRouterService.SnipGlobalHotkeyAction.RecordingMode:
                HandleRecordingModeHotkeyCommand?.Execute().Subscribe();
                break;
            case HotkeyRouterService.SnipGlobalHotkeyAction.TranslateMode:
                SetTranslationModeCommand?.Execute().Subscribe();
                break;
            case HotkeyRouterService.SnipGlobalHotkeyAction.CopyAutoAction:
                AutoActionMode = SnipAutoAction.Copy;
                break;
            case HotkeyRouterService.SnipGlobalHotkeyAction.TextCopyAutoAction:
                AutoActionMode = SnipAutoAction.TextCopy;
                break;
            case HotkeyRouterService.SnipGlobalHotkeyAction.ScrollingCaptureAutoAction:
                AutoActionMode = SnipAutoAction.ScrollingCapture;
                break;
        }
    }

    private bool TryHandleModeSwitchHotkey(string pressedHotkey)
    {
        if (string.IsNullOrWhiteSpace(pressedHotkey) || _mainVm == null || RecState != RecordingState.Idle)
            return false;

        switch (CurrentMode)
        {
            case SnipMode.Screenshot:
                if (StringComparer.OrdinalIgnoreCase.Equals(pressedHotkey, _mainVm.Snip_SwitchToTranslate))
                    { SwitchToTranslateCommand?.Execute().Subscribe(); return true; }
                if (StringComparer.OrdinalIgnoreCase.Equals(pressedHotkey, _mainVm.Snip_SwitchToRecord))
                    { SwitchToRecordCommand?.Execute().Subscribe(); return true; }
                break;
            case SnipMode.Recording:
                if (StringComparer.OrdinalIgnoreCase.Equals(pressedHotkey, _mainVm.Record_SwitchToSnip))
                    { SwitchToSnipCommand?.Execute().Subscribe(); return true; }
                if (StringComparer.OrdinalIgnoreCase.Equals(pressedHotkey, _mainVm.Record_SwitchToTranslate))
                    { SwitchToTranslateCommand?.Execute().Subscribe(); return true; }
                break;
            case SnipMode.Translation:
                if (StringComparer.OrdinalIgnoreCase.Equals(pressedHotkey, _mainVm.Translate_SwitchToSnip))
                    { SwitchToSnipCommand?.Execute().Subscribe(); return true; }
                if (StringComparer.OrdinalIgnoreCase.Equals(pressedHotkey, _mainVm.Translate_SwitchToRecord))
                    { SwitchToRecordCommand?.Execute().Subscribe(); return true; }
                break;
        }
        return false;
    }

    public void HandleCaptureModeRequest(CaptureMode mode)
    {
        switch (mode)
        {
            case CaptureMode.Normal:
                LockSelectedScreenshotSelection = _mainVm?.AutoPinScreenshotSelection == true;
                AutoActionMode = ResolveAutoActionMode(mode, _mainVm?.AutoPinScreenshotSelection == true);
                HandleScreenshotModeHotkeyCommand?.Execute().Subscribe();
                break;
            case CaptureMode.Record:
                LockSelectedScreenshotSelection = false;
                HandleRecordingModeHotkeyCommand?.Execute().Subscribe();
                break;
            case CaptureMode.Pin:
                LockSelectedScreenshotSelection = false;
                HandleActiveActionHotkeyCommand?.Execute().Subscribe();
                break;
            case CaptureMode.Translate:
                LockSelectedScreenshotSelection = false;
                SetTranslationModeCommand?.Execute().Subscribe();
                break;
            case CaptureMode.Copy:
                LockSelectedScreenshotSelection = false;
                AutoActionMode = SnipAutoAction.Copy;
                break;
            case CaptureMode.TextCopy:
                LockSelectedScreenshotSelection = false;
                AutoActionMode = SnipAutoAction.TextCopy;
                break;
            case CaptureMode.ScrollingCapture:
                LockSelectedScreenshotSelection = false;
                AutoActionMode = SnipAutoAction.ScrollingCapture;
                break;
        }
    }

    internal static SnipAutoAction ResolveAutoActionMode(CaptureMode mode, bool autoPinScreenshotSelection)
    {
        return mode switch
        {
            CaptureMode.Normal => SnipAutoAction.None,
            CaptureMode.Copy => SnipAutoAction.Copy,
            CaptureMode.Pin => SnipAutoAction.Pin,
            CaptureMode.Record => SnipAutoAction.EnterRecordMode,
            CaptureMode.TextCopy => SnipAutoAction.TextCopy,
            CaptureMode.ScrollingCapture => SnipAutoAction.ScrollingCapture,
            _ => SnipAutoAction.None
        };
    }


    // Init Method
    private void InitializeActionCommands()
    {
        var canExecuteHotkeys = CreateCanExecuteHotkeys();

        PinCommand = CreateAsyncCommand(ExecutePinActionAsync, nameof(PinCommand), canExecuteHotkeys);

        CopyCommand = CreateAsyncCommand(
            async () =>
            {
                if (CurrentMode != SnipMode.Recording) await Copy();
                else await CopyRecording();
            },
            nameof(CopyCommand),
            canExecuteHotkeys);

        SaveCommand = CreateAsyncCommand(Save, nameof(SaveCommand), canExecuteHotkeys);

        ScrollingCaptureCommand = CreateAsyncCommand(ExecuteScrollingCapture, nameof(ScrollingCaptureCommand), canExecuteHotkeys);

        SetScrollDirectionCommand = CreateCommand<ScrollingCaptureDirection>(
            direction => ScrollingCaptureDirection = direction,
            nameof(SetScrollDirectionCommand),
            canExecuteHotkeys);

        CloseCommand = CreateCommand(Close, nameof(CloseCommand), canExecuteHotkeys);
        DismissOrCloseCommand = CreateCommand(DismissOrClose, nameof(DismissOrCloseCommand), canExecuteHotkeys);

        ToggleModeCommand = CreateCommand(() =>
        {
            if (RecState == RecordingState.Idle) 
            {
                CurrentMode = CurrentMode == SnipMode.Recording ? SnipMode.Screenshot : SnipMode.Recording;
            }
        }, nameof(ToggleModeCommand), canExecuteHotkeys);

        SetCaptureModeCommand = CreateCommand<bool>(isRecord =>
        {
            if (RecState == RecordingState.Idle)
            {
                CurrentMode = isRecord ? SnipMode.Recording : SnipMode.Screenshot;
            }
        }, nameof(SetCaptureModeCommand), canExecuteHotkeys);

        StartRecordingCommand = CreateAsyncCommand(StartRecording, nameof(StartRecordingCommand));

        var canPauseRecordingHotkey = this.WhenAnyValue(
            x => x.RecState,
            x => x.IsInputFocused,
            (rec, textFocus) => rec != RecordingState.Idle && !textFocus);
        PauseRecordingCommand = CreateAsyncCommand(PauseRecording, nameof(PauseRecordingCommand), canPauseRecordingHotkey);
        StopRecordingCommand = CreateAsyncCommand(StopRecording, nameof(StopRecordingCommand));
        CopyRecordingCommand = CreateAsyncCommand(CopyRecording, nameof(CopyRecordingCommand));

        HandleScreenshotModeHotkeyCommand = CreateCommand(() =>
        {
            if (RecState == RecordingState.Idle) 
            {
                CurrentMode = SnipMode.Screenshot;
            }
        }, nameof(HandleScreenshotModeHotkeyCommand), canExecuteHotkeys);

        HandleRecordingModeHotkeyCommand = CreateCommand(() =>
        { 
            if (RecState == RecordingState.Idle) 
            {
                // USER REQUEST: F2 always switches/sets Record Mode, never auto-starts recording
                if (CurrentMode != SnipMode.Recording)
                {
                    CurrentMode = SnipMode.Recording;
                }
            }
        }, nameof(HandleRecordingModeHotkeyCommand), canExecuteHotkeys);

        ToggleSelectionModeCommand = CreateCommand(() =>
        {
            if (RecState != RecordingState.Idle || CurrentMode == SnipMode.Translation)
            {
                return;
            }

            DeactivateDrawingInteraction();
            if (CurrentState == SnipState.Selecting || CurrentState == SnipState.Selected)
            {
                CurrentState = SnipState.Detecting;
                SelectionRect = new Rect(0, 0, 0, 0);
                return;
            }

            ShowTopLoadingBar = false;
            CurrentState = SnipState.Selecting;
            SelectionRect = new Rect(0, 0, 0, 0);
        }, nameof(ToggleSelectionModeCommand), canExecuteHotkeys);

        // F6/F8: current-mode action keys for screenshot/record/translation.
        // F1/F2/F3 are reserved for Screenshot/Record/Translate mode switching while selecting.
        HandleActiveActionHotkeyCommand = CreateCommand(HandleActiveActionHotkey, nameof(HandleActiveActionHotkeyCommand), canExecuteHotkeys);

        SetTranslationModeCommand = CreateCommand(ToggleTranslationMode, nameof(SetTranslationModeCommand), canExecuteHotkeys);

        var canSwitchToSnip = CreateCanSwitchToMode(SnipMode.Screenshot);
        SwitchToSnipCommand = CreateCommand(() => { CurrentMode = SnipMode.Screenshot; }, nameof(SwitchToSnipCommand), canSwitchToSnip);

        var canSwitchToRecord = CreateCanSwitchToMode(SnipMode.Recording);
        SwitchToRecordCommand = CreateCommand(() => { CurrentMode = SnipMode.Recording; }, nameof(SwitchToRecordCommand), canSwitchToRecord);

        var canSwitchToTranslate = CreateCanSwitchToMode(SnipMode.Translation);
        SwitchToTranslateCommand = CreateCommand(EnterTranslationMode, nameof(SwitchToTranslateCommand), canSwitchToTranslate);

        var canRemoveBackground = this.WhenAnyValue(
            x => x.CurrentMode, 
            x => x.ShowProcessingOverlay, 
            (mode, isProc) => mode != SnipMode.Recording && !isProc);

        RemoveBackgroundCommand = CreateAsyncCommand(async () =>
        {
            // Pin first, then run AI
            await Pin(true, false);
        }, nameof(RemoveBackgroundCommand), canRemoveBackground);

        InteractiveRemovalCommand = CreateAsyncCommand(async () =>
        {
            // Pin first, then run interactive AI
            await Pin(false, true);
        }, nameof(InteractiveRemovalCommand), canRemoveBackground);

        ToggleTopmostCommand = CreateCommand(() =>
        {
            IsTopmost = !IsTopmost;
            System.Diagnostics.Debug.WriteLine($"[SnipWindow] Topmost toggled to: {IsTopmost}");
            _mainVm?.SetStatus(IsTopmost ? "Topmost ON" : "Topmost OFF");
        }, nameof(ToggleTopmostCommand));
        
    }

    private IObservable<bool> CreateCanExecuteHotkeys()
    {
        return this.WhenAnyValue(x => x.IsInputFocused, x => !x);
    }

    private IObservable<bool> CreateCanSwitchToMode(SnipMode targetMode)
    {
        return this.WhenAnyValue(
            x => x.CurrentMode,
            x => x.RecState,
            x => x.IsInputFocused,
            (mode, rec, focus) => mode != targetMode && rec == RecordingState.Idle && !focus);
    }

    private async Task ExecutePinActionAsync()
    {
        if (CurrentMode != SnipMode.Recording)
        {
            await Pin(false);
            return;
        }

        bool hasCurrentRecording = !string.IsNullOrEmpty(_currentRecordingPath)
                                   && System.IO.File.Exists(_currentRecordingPath);
        switch (ResolveRecordingPinAction(RecState, CurrentState, hasCurrentRecording))
        {
            case RecordingPinAction.StartRecording:
                await StartRecording();
                break;
            case RecordingPinAction.PinRecording:
                await PinRecording();
                break;
        }
    }

    internal static RecordingPinAction ResolveRecordingPinAction(
        RecordingState recordingState,
        SnipState currentState,
        bool hasCurrentRecording)
    {
        if (recordingState is RecordingState.Recording or RecordingState.Paused)
        {
            return RecordingPinAction.PinRecording;
        }

        if (recordingState != RecordingState.Idle)
        {
            return RecordingPinAction.None;
        }

        // A selected idle region always represents a new recording request.
        // A finalized path from an older SnipWindow must never take precedence.
        if (currentState == SnipState.Selected)
        {
            return RecordingPinAction.StartRecording;
        }

        return hasCurrentRecording
            ? RecordingPinAction.PinRecording
            : RecordingPinAction.None;
    }

    private void HandleActiveActionHotkey()
    {
        // Guard against duplicate dispatch from low-level/global + window-level key routing.
        // Without this, one key press can start and immediately stop recording, producing a 1-frame file.
        var now = DateTime.UtcNow;
        var cooldown = (CurrentMode == SnipMode.Recording)
            ? TimeSpan.FromMilliseconds(1800)
            : TimeSpan.FromMilliseconds(350);
        if ((now - _lastActiveActionHotkeyUtc) < cooldown)
        {
            return;
        }
        _lastActiveActionHotkeyUtc = now;

        System.Diagnostics.Debug.WriteLine($"[SnipWindowViewModel] HandleActiveActionHotkeyCommand invoked. Mode: {CurrentMode}, RecState: {RecState}");
        if (CurrentMode == SnipMode.Translation)
        {
            System.Diagnostics.Debug.WriteLine("[ActiveAction] Translation: toggle results");
            // 翻譯模式：切換結果顯示
            ShowTranslationResults = !ShowTranslationResults;
            return;
        }

        // 錄影模式（正在錄製/暫停）：停止並釘選
        if (CurrentMode == SnipMode.Recording && RecState != RecordingState.Idle)
        {
            // Ignore accidental immediate second trigger right after recording starts
            // (e.g. duplicate key routing / key-repeat burst).
            if (RecState == RecordingState.Recording &&
                (DateTime.UtcNow - _recordingActiveStartUtc) < TimeSpan.FromMilliseconds(900))
            {
                System.Diagnostics.Debug.WriteLine("[ActiveAction] Recording: ignored immediate stop guard");
                return;
            }

            System.Diagnostics.Debug.WriteLine("[ActiveAction] Recording: stop/pin");
            PinCommand?.Execute().Subscribe();
            return;
        }

        // 錄影模式（空閒且已選取）：開始錄影
        if (CurrentMode == SnipMode.Recording && CurrentState == SnipState.Selected)
        {
            System.Diagnostics.Debug.WriteLine("[ActiveAction] Recording: start");
            StartRecordingCommand?.Execute().Subscribe();
            return;
        }

        // 截圖模式或未進入錄影：釘選
        System.Diagnostics.Debug.WriteLine("[SnipWindowViewModel] Invoking PinCommand.");
        PinCommand?.Execute().Subscribe();
    }

    private void ToggleTranslationMode()
    {
        if (RecState != RecordingState.Idle)
        {
            return;
        }

        if (CurrentMode == SnipMode.Translation)
        {
            // 已在翻譯模式，點擊則切換回截圖模式
            CurrentMode = SnipMode.Screenshot;
            return;
        }

        EnterTranslationMode();
    }

    private void EnterTranslationMode()
    {
        // 進入翻譯模式並重置選取狀態
        CurrentMode = SnipMode.Translation;
        CurrentState = SnipState.Detecting;
        SelectionRect = default;
        RefreshInteractionRegion();
        InitializeTranslationToolbarPosition();
        LogTranslationMemoryState("translation-enter-command");
    }

    private ReactiveCommand<Unit, Unit> CreateCommand(Action execute, string commandName)
    {
        return ReactiveCommandLifecycleHelper.CreateCommand(
            execute,
            canExecute: null,
            commandName,
            _disposables,
            nameof(SnipWindowViewModel));
    }

    private ReactiveCommand<Unit, Unit> CreateCommand(Action execute, string commandName, IObservable<bool> canExecute)
    {
        return ReactiveCommandLifecycleHelper.CreateCommand(
            execute,
            canExecute,
            commandName,
            _disposables,
            nameof(SnipWindowViewModel));
    }

    private ReactiveCommand<TParam, Unit> CreateCommand<TParam>(Action<TParam> execute, string commandName, IObservable<bool> canExecute)
    {
        return ReactiveCommandLifecycleHelper.CreateCommand(
            execute,
            canExecute,
            commandName,
            _disposables,
            nameof(SnipWindowViewModel));
    }

    private ReactiveCommand<Unit, Unit> CreateAsyncCommand(Func<Task> execute, string commandName)
    {
        return ReactiveCommandLifecycleHelper.CreateAsyncCommand(
            execute,
            canExecute: null,
            commandName,
            _disposables,
            nameof(SnipWindowViewModel));
    }

    private ReactiveCommand<Unit, Unit> CreateAsyncCommand(Func<Task> execute, string commandName, IObservable<bool> canExecute)
    {
        return ReactiveCommandLifecycleHelper.CreateAsyncCommand(
            execute,
            canExecute,
            commandName,
            _disposables,
            nameof(SnipWindowViewModel));
    }

    private async Task StartRecording()
    {
        await ExecuteStartRecordingAsync();
    }

    private async Task PauseRecording()
    {
        await ExecutePauseRecordingAsync();
    }

    private async Task StopRecording()
    {
        await ExecuteStopRecordingAsync();
    }

    private bool _isProcessingRecording = false;

    private async Task CopyRecording()
    {
        await ExecuteCopyRecordingAsync();
    }

    private async Task PinRecording()
    {
        await ExecutePinRecordingAsync();
    }

    private async Task Copy() 
    {
        await ExecuteCopyAsync();
    }

    private async Task Save() 
    {
        await ExecuteSaveAsync();
    }
    
    private async Task Pin(bool runAI = false, bool initialInteractive = false)
    {
        await ExecutePinAsync(runAI, initialInteractive);
    }

    private void Close()
    {
        // During manual scrolling capture the Close key (Esc) cancels the session.
        if (_manualScrollActive)
        {
            FinishManualScrollCapture(cancelled: true);
            return;
        }

        _scanCts?.Cancel();
        CloseAction?.Invoke();
    }

    /// <summary>
    /// The single Esc / dismiss decision for the snip overlay, invoked by BOTH the Esc key-binding
    /// (screenshot / recording modes) and the low-level keyboard hook (translation / unfocused capture), so the
    /// behaviour can't diverge. Two-stage in box-select: a finalized box is cleared first (staying in manual
    /// draw mode) and only a second Esc — with no box — closes the overlay.
    /// </summary>
    public void DismissOrClose()
    {
        if (_manualScrollActive) { FinishManualScrollCapture(cancelled: true); return; }
        if (IsEnteringText) { CancelTextEntryCommand.Execute(Unit.Default).Subscribe(); return; }
        if (RecState != RecordingState.Idle) { return; }
        if (IsTranslationMode) { Close(); return; }
        if (IsDrawingMode) { IsDrawingMode = false; return; }
        if (ShouldClearBoxToDraw(CurrentState, SelectionRect.Width > 0 && SelectionRect.Height > 0))
        {
            SelectionRect = new Rect(0, 0, 0, 0);
            CurrentState = SnipState.Selecting;
            // Back in the draw-ready state, re-activate the OCR auto-scan just like the auto-detect (Detecting)
            // state does — the transition into Selecting cancelled it, so kick off a fresh scan.
            if (ShouldTriggerAutoScan())
            {
                // Call RunOCRScanAsync directly, NOT via TriggerAutoScanCommand: the just-cancelled Detecting
                // scan can still be running its CPU-bound OCR DetectText (uninterruptible), which keeps the
                // ReactiveCommand "executing" so Execute() is silently skipped — the reason the Esc-clear
                // re-scan never actually ran. RunOCRScanAsync self-cancels the prior scan and always starts.
                RunOCRScanAsync().Forget("Snip.EscRescan");
            }
            return;
        }
        Close();
    }

    public void HandleRightClick()
    {
        if (RecState != RecordingState.Idle) return;

        // 翻譯模式下右鍵點擊空白處不關閉視窗 (避免與右鍵刪除選取框衝突)
        if (CurrentMode == SnipMode.Translation) return;

        if (CurrentState == SnipState.Selecting || CurrentState == SnipState.Selected)
        {
            CurrentState = SnipState.Detecting;
            SelectionRect = new Rect(0,0,0,0);
        }
        else
        {
            Close();
        }
    }
}
