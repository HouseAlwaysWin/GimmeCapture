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
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.ViewModels.Shared;
using GimmeCapture.Views.Floating;

namespace GimmeCapture.ViewModels.Main;

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
        nameof(IsAiDetectedRectPreviewVisible)
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
        nameof(RectangleTooltip),
        nameof(EllipseTooltip),
        nameof(ArrowTooltip),
        nameof(LineTooltip),
        nameof(PenTooltip),
        nameof(TextTooltip),
        nameof(MosaicTooltip),
        nameof(BlurTooltip),
        nameof(FullscreenSelectTooltip),
        nameof(SnipTooltip),
        nameof(RecordTooltip),
        nameof(TranslateTooltip),
        nameof(HideTranslationResultsTooltip),
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

        this.RaiseAndSetIfChanged(ref _currentMode, value, nameof(CurrentMode));

        // Logic from old IsRecordingMode / IsTranslationMode setters
        if (value == SnipMode.Translation)
        {
            // 截圖/錄影的 AI 自動選取與視窗候選框不適用於翻譯模式
            ClearAiScanOverlayState();

            // 進入翻譯模式：啟用遮罩並更新挖空區域
            SelectionRect = new Rect(0, 0, 0, 0); // 確保清空標準選取框，避免干擾挖空
            IsMaskVisible = true;
            RaiseProperties(nameof(MaskOpacity));
            UpdateMask();
            StartAutoDetectLoop();
            StartTranslationWarmup();
        }
        else if (oldMode == SnipMode.Translation)
        {
            // 退出翻譯模式：恢復遮罩
            ResetTranslationToolbarAfterLeavingTranslationMode();
            IsMaskVisible = true;
            // 清除多重選取
            UserSelections.Clear();
            // 關閉自動偵測
            IsGlobalAutoDetectEnabled = false;
            
            RaiseProperties(nameof(MaskOpacity));
            UpdateMask();
            StopAutoDetectLoop();
            CancelTranslationWarmup();
        }

        // Common updates
        SelectionBorderColor = _mainVm?.ThemeColor ?? Colors.Yellow;

        // Notify all related properties
        RaiseProperties(_modeStatePropertyNames);

        // Notify hotkeys and tooltips
        RaiseProperties(_modeHotkeyPropertyNames);
        RaiseProperties(_modeTooltipPropertyNames);
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
    public bool IsToolbarVisible =>
        CurrentMode == SnipMode.Translation
        || (CurrentState == SnipState.Selected && !IsRecordingFinalizing);

    /// <summary>
    /// 使用者是否「看得到」工具列（未停到螢幕外）；Win32 命中與 Canvas 互動以此為準。
    /// </summary>
    public bool IsToolbarShownOnScreen =>
        ShowToolbar && (CurrentMode == SnipMode.Translation || (CurrentState == SnipState.Selected && !IsRecordingFinalizing));

    public string ModeDisplayName => CurrentMode switch
    {
        SnipMode.Translation => LocalizationService.Instance["CaptureModeTranslation"] ?? "Translation",
        SnipMode.Recording => LocalizationService.Instance["CaptureModeRecord"],
        _ => LocalizationService.Instance["CaptureModeNormal"]
    };

    // True when actively recording (not idle, not paused) - used to hide selection border
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
            SetRecordingSelectionChromeHidden(value || RecState != RecordingState.Idle);
        }
    }
    private bool _forceHideRecordingSelectionChrome;

    private void SetRecordingSelectionChromeHidden(bool hidden)
    {
        if (_forceHideRecordingSelectionChrome == hidden) return;
        _forceHideRecordingSelectionChrome = hidden;
        RaiseProperties(nameof(HideFrameBorder), nameof(HideSelectionDecoration));
    }

    // Action Helpers
    public bool HideSelectionDecoration 
    {
        get
        {
            if (_forceHideRecordingSelectionChrome || IsRecordingFinalizing)
            {
                return true;
            }
            bool hide = CurrentMode == SnipMode.Recording ? (_mainVm?.HideRecordSelectionDecoration ?? false) : (_mainVm?.HideSnipSelectionDecoration ?? false);
            System.Diagnostics.Debug.WriteLine($"[SnipWindow] HideSelectionDecoration queried: {hide} (CurrentMode: {CurrentMode}, RecState: {RecState})");
            return hide;
        }
    }

    public bool HideFrameBorder 
    {
        get
        {
            if (_forceHideRecordingSelectionChrome || IsRecordingFinalizing)
            {
                return true;
            }
            bool hide = CurrentMode == SnipMode.Recording ? (_mainVm?.HideRecordSelectionBorder ?? false) : (_mainVm?.HideSnipSelectionBorder ?? false);
            System.Diagnostics.Debug.WriteLine($"[SnipWindow] HideFrameBorder queried: {hide} (CurrentMode: {CurrentMode}, RecState: {RecState})");
            return hide;
        }
    }

    private int _autoActionMode = 0; // 0=Normal, 1=Copy, 2=Pin
    public int AutoActionMode
    {
        get => _autoActionMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoActionMode, value);
            if (value > 0 && CurrentState == SnipState.Selected)
            {
                TriggerAutoAction();
            }
        }
    }

    private void TriggerAutoAction()
    {
        if (AutoActionMode == 1) // Copy
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () => await Copy());
        }
        else if (AutoActionMode == 2) // Pin
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () => await Pin());
        }
        else if (AutoActionMode == 3) // Record mode entry, do NOT auto-start
        {
             if (CurrentMode != SnipMode.Recording) CurrentMode = SnipMode.Recording;
             // USER REQUEST: Selection only, record manually or via F3
        }
    }

    public RecordingState RecState => _recordingService?.State ?? RecordingState.Idle;

    private string? _currentRecordingPath;

    // Commands (Partial declarations not needed if initialized in constructor)
    // But we need to define the properties here to be grouped

    public ReactiveCommand<Unit, Unit> CopyCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> PinCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> CloseCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleModeCommand { get; set; } = null!;
    public ReactiveCommand<bool, Unit> SetCaptureModeCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SetTranslationModeCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> StartRecordingCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> PauseRecordingCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> StopRecordingCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> CopyRecordingCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> HandleScreenshotModeHotkeyCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> HandleRecordingModeHotkeyCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> HandleActiveActionHotkeyCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SwitchToSnipCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SwitchToRecordCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SwitchToTranslateCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> RemoveBackgroundCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> InteractiveRemovalCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleTopmostCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleMaskCommand { get; set; } = null!;

    public void HandleGlobalHotkey(int id)
    {
        if (_mainVm == null) return;
        
        string pressedHotkey = _mainVm.HotkeyRouterService.GetPressedHotkeyText(id, _mainVm);

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
                AutoActionMode = 1;
                if (CurrentState == SnipState.Selected) TriggerAutoAction();
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

    public void HandleCaptureModeRequest(MainWindowViewModel.CaptureMode mode)
    {
        switch (mode)
        {
            case MainWindowViewModel.CaptureMode.Normal:
                HandleScreenshotModeHotkeyCommand?.Execute().Subscribe();
                break;
            case MainWindowViewModel.CaptureMode.Record:
                HandleRecordingModeHotkeyCommand?.Execute().Subscribe();
                break;
            case MainWindowViewModel.CaptureMode.Pin:
                HandleActiveActionHotkeyCommand?.Execute().Subscribe();
                break;
            case MainWindowViewModel.CaptureMode.Translate:
                SetTranslationModeCommand?.Execute().Subscribe();
                break;
            case MainWindowViewModel.CaptureMode.Copy:
                AutoActionMode = 1;
                if (CurrentState == SnipState.Selected) TriggerAutoAction();
                break;
        }
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
        
        CloseCommand = CreateCommand(Close, nameof(CloseCommand), canExecuteHotkeys);

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
        PauseRecordingCommand = CreateAsyncCommand(PauseRecording, nameof(PauseRecordingCommand));
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

        // F3: 模式選擇器
        // 截圖模式 -> F3 -> Pin
        // 錄影模式 -> F3 -> Pin  
        // 翻譯模式 -> F3 -> 無動作
        // 未進入模式 (Detecting) -> F3 -> 進入翻譯模式
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
        
        ToggleMaskCommand = CreateCommand(() =>
        {
            IsMaskVisible = !IsMaskVisible;
            System.Diagnostics.Debug.WriteLine($"[SnipWindow] Mask toggled to: {IsMaskVisible}");
        }, nameof(ToggleMaskCommand));
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

        if (RecState == RecordingState.Recording || RecState == RecordingState.Paused)
        {
            await PinRecording();
            return;
        }

        if (RecState != RecordingState.Idle)
        {
            return;
        }

        var lastPath = _recordingService?.LastRecordingPath;
        if (!string.IsNullOrEmpty(lastPath) && System.IO.File.Exists(lastPath))
        {
            await PinRecording();
            _recordingService?.ClearLastRecording();
            return;
        }

        if (CurrentState == SnipState.Selected)
        {
            await StartRecording();
        }
    }

    private void HandleActiveActionHotkey()
    {
        System.Diagnostics.Debug.WriteLine($"[SnipWindowViewModel] HandleActiveActionHotkeyCommand invoked. Mode: {CurrentMode}, RecState: {RecState}");
        if (CurrentMode == SnipMode.Translation)
        {
            // 翻譯模式：切換結果顯示
            ShowTranslationResults = !ShowTranslationResults;
            return;
        }

        // 錄影模式（正在錄製/暫停）：停止並釘選
        if (CurrentMode == SnipMode.Recording && RecState != RecordingState.Idle)
        {
            PinCommand?.Execute().Subscribe();
            return;
        }

        // 錄影模式（空閒且已選取）：開始錄影
        if (CurrentMode == SnipMode.Recording && CurrentState == SnipState.Selected)
        {
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
        // Translation relies on Win32 passthrough hit-test; keep drawing mode off to avoid stale state from screenshot mode.
        IsDrawingMode = false;
        CurrentState = SnipState.Detecting;
        SelectionRect = default;
        UpdateMask();
        InitializeTranslationToolbarPosition();
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
        _scanCts?.Cancel();
        CloseAction?.Invoke(); 
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
