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
using GimmeCapture.Views.Floating;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
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
            // 進入翻譯模式：啟用遮罩並更新挖空區域
            SelectionRect = new Rect(0, 0, 0, 0); // 確保清空標準選取框，避免干擾挖空
            IsMaskVisible = true;
            this.RaisePropertyChanged(nameof(MaskOpacity));
            UpdateMask();
            StartAutoDetectLoop();
        }
        else if (oldMode == SnipMode.Translation)
        {
            // 退出翻譯模式：恢復遮罩
            IsMaskVisible = true;
            // 清除多重選取
            UserSelections.Clear();
            // 關閉自動偵測
            IsGlobalAutoDetectEnabled = false;
            
            this.RaisePropertyChanged(nameof(MaskOpacity));
            UpdateMask();
            StopAutoDetectLoop();
        }

        // Common updates
        SelectionBorderColor = _mainVm?.ThemeColor ?? Colors.Yellow;

        // Notify all related properties
        this.RaisePropertyChanged(nameof(IsRecordingMode));
        this.RaisePropertyChanged(nameof(IsTranslationMode));
        this.RaisePropertyChanged(nameof(IsScreenshotMode));
        this.RaisePropertyChanged(nameof(HideFrameBorder));
        this.RaisePropertyChanged(nameof(HideSelectionDecoration));
        this.RaisePropertyChanged(nameof(ModeDisplayName));
        this.RaisePropertyChanged(nameof(IsToolbarVisible));
        
        // Notify hotkeys and tooltips
        this.RaisePropertyChanged(nameof(CopyHotkey));
        this.RaisePropertyChanged(nameof(UndoHotkey));
        this.RaisePropertyChanged(nameof(RedoHotkey));
        this.RaisePropertyChanged(nameof(ClearHotkey));
        this.RaisePropertyChanged(nameof(SaveHotkey));
        this.RaisePropertyChanged(nameof(CloseHotkey));
        this.RaisePropertyChanged(nameof(RectangleHotkey));
        this.RaisePropertyChanged(nameof(EllipseHotkey));
        this.RaisePropertyChanged(nameof(ArrowHotkey));
        this.RaisePropertyChanged(nameof(LineHotkey));
        this.RaisePropertyChanged(nameof(PenHotkey));
        this.RaisePropertyChanged(nameof(TextHotkey));
        this.RaisePropertyChanged(nameof(MosaicHotkey));
        this.RaisePropertyChanged(nameof(BlurHotkey));
        this.RaisePropertyChanged(nameof(ActiveActionHotkey));
        this.RaisePropertyChanged(nameof(ActiveToolbarHotkey));

        this.RaisePropertyChanged(nameof(UndoTooltip));
        this.RaisePropertyChanged(nameof(RedoTooltip));
        this.RaisePropertyChanged(nameof(ClearTooltip));
        this.RaisePropertyChanged(nameof(SaveTooltip));
        this.RaisePropertyChanged(nameof(CopyTooltip));
        this.RaisePropertyChanged(nameof(PinTooltip));
        this.RaisePropertyChanged(nameof(RectangleTooltip));
        this.RaisePropertyChanged(nameof(EllipseTooltip));
        this.RaisePropertyChanged(nameof(ArrowTooltip));
        this.RaisePropertyChanged(nameof(LineTooltip));
        this.RaisePropertyChanged(nameof(PenTooltip));
        this.RaisePropertyChanged(nameof(TextTooltip));
        this.RaisePropertyChanged(nameof(MosaicTooltip));
        this.RaisePropertyChanged(nameof(BlurTooltip));
        this.RaisePropertyChanged(nameof(TranslateAllHotkey));
        this.RaisePropertyChanged(nameof(ScanAllHotkey));
        this.RaisePropertyChanged(nameof(ClearAllHotkey));
        this.RaisePropertyChanged(nameof(ToggleSelectHotkey));
        this.RaisePropertyChanged(nameof(AutoDetectHotkey));

        this.RaisePropertyChanged(nameof(HideTranslationResultsTooltip));
        this.RaisePropertyChanged(nameof(TranslateAllTooltip));
        this.RaisePropertyChanged(nameof(ScanAllTooltip));
        this.RaisePropertyChanged(nameof(ClearAllTooltip));
        this.RaisePropertyChanged(nameof(ToggleSelectTooltip));
        this.RaisePropertyChanged(nameof(AutoDetectTooltip));
        this.RaisePropertyChanged(nameof(ToggleToolbarTooltip));
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
                sel.IsAutoDetectEnabled = value;
            }
            // 如果開啟，喚醒背景迴圈，或者直接依靠現有的 Loop
            if (value && CurrentMode == SnipMode.Translation)
            {
                StartAutoDetectLoop();
            }
        }
    }

    /// <summary>
    /// 工具列是否可見：翻譯模式始終可見，其他模式需要在 Selected 狀態且未 Finalizing
    /// </summary>
    public bool IsToolbarVisible => ShowToolbar && (CurrentMode == SnipMode.Translation || (CurrentState == SnipState.Selected && !IsRecordingFinalizing));

    public string ModeDisplayName => CurrentMode switch
    {
        SnipMode.Translation => LocalizationService.Instance["CaptureModeTranslation"] ?? "Translation",
        SnipMode.Recording => LocalizationService.Instance["CaptureModeRecord"],
        _ => LocalizationService.Instance["CaptureModeNormal"]
    };

    // True when actively recording (not idle, not paused) - used to hide selection border
    public bool IsRecordingActive => _recordingService?.State == RecordingState.Recording;

    // Current recording format (gif, mp4, webm, etc.)
    public string RecordFormat => _mainVm?.RecordFormat ?? "mp4";

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

    private bool _isRecordingFinalizing;
    public bool IsRecordingFinalizing
    {
        get => _isRecordingFinalizing;
        set
        {
            this.RaiseAndSetIfChanged(ref _isRecordingFinalizing, value);
            this.RaisePropertyChanged(nameof(IsToolbarVisible));
        }
    }

    // Action Helpers
    public bool HideSelectionDecoration 
    {
        get
        {
            // Always show during selection phase
            if (CurrentMode == SnipMode.Recording && RecState == RecordingState.Idle) return false;
            
            bool hide = CurrentMode == SnipMode.Recording ? (_mainVm?.HideRecordSelectionDecoration ?? false) : (_mainVm?.HideSnipSelectionDecoration ?? false);
            System.Diagnostics.Debug.WriteLine($"[SnipWindow] HideSelectionDecoration queried: {hide} (CurrentMode: {CurrentMode}, RecState: {RecState})");
            return hide;
        }
    }

    public bool HideFrameBorder 
    {
        get
        {
            // For Recording Mode: keep border visible on screen for targeting.
            // Border visuals are rendered outside SelectionRect, so recorded region remains clean.
            if (CurrentMode == SnipMode.Recording) return false;
            
            bool hide = _mainVm?.HideSnipSelectionBorder ?? false;
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
    public ReactiveCommand<Unit, Unit> RemoveBackgroundCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> InteractiveRemovalCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleTopmostCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleMaskCommand { get; set; } = null!;

    public void HandleGlobalHotkey(int id)
    {
        if (_mainVm == null) return;
        
        string pressedHotkey = _mainVm.HotkeyRouterService.GetPressedHotkeyText(id, _mainVm);

        System.Diagnostics.Debug.WriteLine($"[SnipWindowViewModel] HandleGlobalHotkey ID={id}, Pressed={pressedHotkey}, ActiveAction={ActiveActionHotkey}, ActiveToolbar={ActiveToolbarHotkey}");

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
        var canExecuteHotkeys = this.WhenAnyValue(x => x.IsInputFocused, x => !x);

        PinCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (CurrentMode != SnipMode.Recording)
            {
                await Pin(false);
            }
            else 
            {
                if (RecState == RecordingState.Recording || RecState == RecordingState.Paused)
                {
                    await PinRecording();
                }
                else if (RecState == RecordingState.Idle)
                {
                     var lastPath = _recordingService?.LastRecordingPath;
                     if (!string.IsNullOrEmpty(lastPath) && System.IO.File.Exists(lastPath))
                     {
                          await PinRecording();
                          _recordingService?.ClearLastRecording();
                     }
                     else if (CurrentState == SnipState.Selected)
                     {
                         await StartRecording();
                     }
                }
            }
        }, canExecuteHotkeys);
        PinCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"PinCommand error: {ex}"));

        CopyCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (CurrentMode != SnipMode.Recording) await Copy();
            else await CopyRecording();
        }, this.WhenAnyValue(x => x.IsInputFocused, x => !x));
        CopyCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"CopyCommand error: {ex}"));

        SaveCommand = ReactiveCommand.CreateFromTask(Save, canExecuteHotkeys);
        SaveCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"SaveCommand error: {ex}"));
        
        CloseCommand = ReactiveCommand.Create(Close, canExecuteHotkeys);
        CloseCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"CloseCommand error: {ex}"));

        ToggleModeCommand = ReactiveCommand.Create(() => 
        {
            if (RecState == RecordingState.Idle) 
            {
                CurrentMode = CurrentMode == SnipMode.Recording ? SnipMode.Screenshot : SnipMode.Recording;
            }
        }, canExecuteHotkeys);
        ToggleModeCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        SetCaptureModeCommand = ReactiveCommand.Create<bool>(isRecord => 
        {
            if (RecState == RecordingState.Idle)
            {
                CurrentMode = isRecord ? SnipMode.Recording : SnipMode.Screenshot;
            }
        }, canExecuteHotkeys);
        SetCaptureModeCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        StartRecordingCommand = ReactiveCommand.CreateFromTask(StartRecording);
        StartRecordingCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        PauseRecordingCommand = ReactiveCommand.CreateFromTask(PauseRecording);
        PauseRecordingCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        StopRecordingCommand = ReactiveCommand.CreateFromTask(StopRecording);
        StopRecordingCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        CopyRecordingCommand = ReactiveCommand.CreateFromTask(CopyRecording);
        CopyRecordingCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        HandleScreenshotModeHotkeyCommand = ReactiveCommand.Create(() => { 
            if (RecState == RecordingState.Idle) 
            {
                CurrentMode = SnipMode.Screenshot;
            }
        }, canExecuteHotkeys);
        HandleRecordingModeHotkeyCommand = ReactiveCommand.Create(() => 
        { 
            if (RecState == RecordingState.Idle) 
            {
                // USER REQUEST: F2 always switches/sets Record Mode, never auto-starts recording
                if (CurrentMode != SnipMode.Recording)
                {
                    CurrentMode = SnipMode.Recording;
                }
            }
        }, canExecuteHotkeys);

        // F3: 模式選擇器
        // 截圖模式 -> F3 -> Pin
        // 錄影模式 -> F3 -> Pin  
        // 翻譯模式 -> F3 -> 無動作
        // 未進入模式 (Detecting) -> F3 -> 進入翻譯模式
        HandleActiveActionHotkeyCommand = ReactiveCommand.Create(() => 
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
                if (PinCommand != null)
                    PinCommand.Execute().Subscribe();
                return;
            }

            // 錄影模式（空閒且已選取）：開始錄影
            if (CurrentMode == SnipMode.Recording && RecState == RecordingState.Idle && CurrentState == SnipState.Selected)
            {
                if (StartRecordingCommand != null)
                    StartRecordingCommand.Execute().Subscribe();
                return;
            }
            
            // 截圖模式或未進入錄影：釘選
            if (PinCommand != null)
            {
                System.Diagnostics.Debug.WriteLine($"[SnipWindowViewModel] Invoking PinCommand.");
                PinCommand.Execute().Subscribe();
            }
        }, canExecuteHotkeys);
        HandleActiveActionHotkeyCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"HandleActiveActionHotkey error: {ex}"));

        SetTranslationModeCommand = ReactiveCommand.Create(() =>
        {
            if (RecState == RecordingState.Idle)
            {
                if (CurrentMode == SnipMode.Translation)
                {
                    // 已在翻譯模式，點擊則切換回截圖模式
                    CurrentMode = SnipMode.Screenshot;
                }
                else
                {
                    // 進入翻譯模式
                    CurrentMode = SnipMode.Translation;
                    // 重置選取狀態
                    CurrentState = SnipState.Detecting;
                    SelectionRect = default;
                    InitializeTranslationToolbarPosition();
                }
            }
        }, canExecuteHotkeys);
        SetTranslationModeCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        var canRemoveBackground = this.WhenAnyValue(
            x => x.CurrentMode, 
            x => x.ShowProcessingOverlay, 
            (mode, isProc) => mode != SnipMode.Recording && !isProc);

        RemoveBackgroundCommand = ReactiveCommand.CreateFromTask(async () => {
             // Pin first, then Run AI
             await Pin(true, false);
        }, canRemoveBackground);
        RemoveBackgroundCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        InteractiveRemovalCommand = ReactiveCommand.CreateFromTask(async () => {
             // Pin first, then Run Interactive AI
             await Pin(false, true);
        }, canRemoveBackground);
        InteractiveRemovalCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        ToggleTopmostCommand = ReactiveCommand.Create(() => 
        {
            IsTopmost = !IsTopmost;
            System.Diagnostics.Debug.WriteLine($"[SnipWindow] Topmost toggled to: {IsTopmost}");
            _mainVm?.SetStatus(IsTopmost ? "Topmost ON" : "Topmost OFF");
        });
        
        ToggleMaskCommand = ReactiveCommand.Create(() => 
        {
            IsMaskVisible = !IsMaskVisible;
            System.Diagnostics.Debug.WriteLine($"[SnipWindow] Mask toggled to: {IsMaskVisible}");
        });
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
