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
            if (value && IsTranslationMode)
            {
                StartAutoDetectLoop();
            }
        }
    }

    /// <summary>
    /// 工具列是否可見：翻譯模式始終可見，其他模式需要在 Selected 狀態且未 Finalizing
    /// </summary>
    public bool IsToolbarVisible => ShowToolbar && (IsTranslationMode || (CurrentState == SnipState.Selected && !IsRecordingFinalizing));

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
            if (IsRecordingMode && RecState == RecordingState.Idle) return false;
            
            bool hide = IsRecordingMode ? (_mainVm?.HideRecordSelectionDecoration ?? false) : (_mainVm?.HideSnipSelectionDecoration ?? false);
            System.Diagnostics.Debug.WriteLine($"[SnipWindow] HideSelectionDecoration queried: {hide} (IsRecordingMode: {IsRecordingMode}, RecState: {RecState})");
            return hide;
        }
    }

    public bool HideFrameBorder 
    {
        get
        {
            // For Recording Mode: ALWAYS show the border on screen, so the user knows what region is being captured.
            // The actual removal of the border from the output video is handled by FFmpeg cropping during StartRecording if _mainVm.HideRecordSelectionBorder is true.
            if (IsRecordingMode) return false;
            
            bool hide = _mainVm?.HideSnipSelectionBorder ?? false;
            System.Diagnostics.Debug.WriteLine($"[SnipWindow] HideFrameBorder queried: {hide} (IsRecordingMode: {IsRecordingMode}, RecState: {RecState})");
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
    public ReactiveCommand<Unit, Unit> HandleF1Command { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> HandleF2Command { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> HandleF3Command { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> RemoveBackgroundCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleTopmostCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleMaskCommand { get; set; } = null!;

    public void HandleGlobalHotkey(int id)
    {
        if (_mainVm == null) return;
        
        // 找出觸發此全域 ID 的熱鍵字串
        string pressedHotkey = string.Empty;
        if (id == 9000) pressedHotkey = _mainVm.SnipHotkey;
        else if (id == 9003) pressedHotkey = _mainVm.RecordHotkey;
        else if (id == 9004) pressedHotkey = _mainVm.TranslateHotkey;

        System.Diagnostics.Debug.WriteLine($"[SnipWindowViewModel] HandleGlobalHotkey ID={id}, Pressed={pressedHotkey}, ActiveAction={ActiveActionHotkey}, ActiveToolbar={ActiveToolbarHotkey}");

        // 若該熱鍵恰好與當前模式的動作熱鍵相同（例如 F3），則優先執行本地動作
        if (!string.IsNullOrEmpty(pressedHotkey))
        {
            if (pressedHotkey == ActiveActionHotkey && HandleF3Command != null)
            {
                System.Diagnostics.Debug.WriteLine($"[SnipWindowViewModel] Intercepted ActionHotkey match. Firing HandleF3Command.");
                HandleF3Command.Execute().Subscribe();
                return;
            }
            if (pressedHotkey == ActiveToolbarHotkey && ToggleToolbarCommand != null)
            {
                ToggleToolbarCommand.Execute().Subscribe();
                return;
            }
        }

        // 使用 ID 進行明確路由 (對應 MainWindowViewModel.ID_*)
        switch (id)
        {
            case 100: // ID_SNIP
                if (HandleF1Command != null) HandleF1Command.Execute().Subscribe();
                break;
            case 101: // ID_RECORD
                if (HandleF2Command != null) HandleF2Command.Execute().Subscribe();
                break;
            case 102: // ID_PIN (不再設定全域，保留防呆)
                if (HandleF3Command != null) HandleF3Command.Execute().Subscribe();
                break;
            case 103: // ID_TRANSLATE
                if (SetTranslationModeCommand != null) SetTranslationModeCommand.Execute().Subscribe();
                break;
            case 104: // ID_COPY (不再設定全域，保留防呆)
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
            if (!IsRecordingMode)
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
            if (!IsRecordingMode) await Copy();
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
                CurrentMode = IsRecordingMode ? SnipMode.Screenshot : SnipMode.Recording;
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

        HandleF1Command = ReactiveCommand.Create(() => { 
            if (RecState == RecordingState.Idle) 
            {
                CurrentMode = SnipMode.Screenshot;
            }
        }, canExecuteHotkeys);
        HandleF2Command = ReactiveCommand.Create(() => 
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
        HandleF3Command = ReactiveCommand.Create(() => 
        {
            System.Diagnostics.Debug.WriteLine($"[SnipWindowViewModel] HandleF3Command invoked. Mode: Trans={IsTranslationMode}, Rec={IsRecordingMode}");
            if (IsTranslationMode)
            {
                // 翻譯模式：切換結果顯示
                ShowTranslationResults = !ShowTranslationResults;
                return;
            }
            
            // 錄影模式（正在錄製/暫停）：停止並釘選
            if (IsRecordingMode && RecState != RecordingState.Idle)
            {
                if (PinCommand != null)
                    PinCommand.Execute().Subscribe();
                return;
            }

            // 錄影模式（空閒且已選取）：開始錄影
            if (IsRecordingMode && RecState == RecordingState.Idle && CurrentState == SnipState.Selected)
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
        HandleF3Command.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"HandleF3 error: {ex}"));

        SetTranslationModeCommand = ReactiveCommand.Create(() =>
        {
            if (RecState == RecordingState.Idle)
            {
                if (IsTranslationMode)
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
            x => x.IsRecordingMode, 
            x => x.ShowProcessingOverlay, 
            (isRec, isProc) => !isRec && !isProc);

        RemoveBackgroundCommand = ReactiveCommand.CreateFromTask(async () => {
             // Pin first, then Run AI
             await Pin(true);
        }, canRemoveBackground);
        RemoveBackgroundCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

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
        // Cancel any pending AI scans immediately
        _scanCts?.Cancel();
        _isLocalProcessing = false;
        ShowProcessingOverlay = false;
        ProcessingText = string.Empty;

        if (_recordingService == null || _mainVm == null) return;

        // Check if FFmpeg is available
        if (!_mainVm.FfmpegDownloader.IsFFmpegAvailable())
        {
            if (!_mainVm.FfmpegDownloader.IsDownloading)
            {
                // Trigger download if not started
                _ = _mainVm.FfmpegDownloader.EnsureFFmpegAsync();
            }
            
            _mainVm.SetStatus("FFmpegNotReady");
            return;
        }
        
        string format = _mainVm.RecordFormat?.ToLowerInvariant() ?? "mp4";

        // Use TempFolder setting if available, otherwise local Temp folder in app directory
        string tempDir = _mainVm.TempDirectory;
        if (string.IsNullOrEmpty(tempDir))
        {
            tempDir = System.IO.Path.Combine(_mainVm.AppSettingsService.BaseDataDirectory, "Temp");
        }
        
        try { System.IO.Directory.CreateDirectory(tempDir); } catch { }

        if (_mainVm.UseFixedRecordPath && !string.IsNullOrEmpty(_mainVm.VideoSaveDirectory))
        {
             // Ensure directory exists
             try { System.IO.Directory.CreateDirectory(_mainVm.VideoSaveDirectory); } catch { }
             string fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.{format}";
             _currentRecordingPath = System.IO.Path.Combine(_mainVm.VideoSaveDirectory, fileName);
        }
        else
        {
             _currentRecordingPath = System.IO.Path.Combine(tempDir, $"GimmeCapture_{Guid.NewGuid()}.{format}");
        }
        
        var region = SelectionRect;
        
        // Shrink region by the thickness of the border so the coloured border is excluded from the recorded video
        // (Only if the user checked "Hide Border during Recording" in settings)
        if (_mainVm != null && _mainVm.HideRecordSelectionBorder)
        {
            double borderThick = SelectionBorderThickness;
            if (region.Width > borderThick * 2 && region.Height > borderThick * 2)
            {
                region = new Avalonia.Rect(
                    region.X + borderThick,
                    region.Y + borderThick,
                    region.Width - borderThick * 2,
                    region.Height - borderThick * 2
                );
            }
        }
        
        // Ensure size is even for ffmpeg
        if (region.Width % 2 != 0) region = region.WithWidth(region.Width - 1);
        if (region.Height % 2 != 0) region = region.WithHeight(region.Height - 1);

        if (await _recordingService.StartAsync(region, _currentRecordingPath, _mainVm!.RecordFormat ?? "mp4", _mainVm.ShowRecordCursor, ScreenOffset, VisualScaling, _mainVm.RecordFPS))
        {
            RecordingDuration = TimeSpan.Zero;
            
            _recordTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _recordTimer.Tick += (s, e) => {
                if (RecState == RecordingState.Recording)
                    RecordingDuration = RecordingDuration.Add(TimeSpan.FromSeconds(1));
            };
            _recordTimer.Start();
        }
    }

    private async Task PauseRecording()
    {
        if (_recordingService == null) return;
        if (RecState == RecordingState.Recording) await _recordingService.PauseAsync();
        else if (RecState == RecordingState.Paused) await _recordingService.ResumeAsync();
    }

    private async Task StopRecording()
    {
        if (_recordingService == null || _mainVm == null) return;
        
        _recordTimer?.Stop();
        await _recordingService.StopAsync();

        // Use the actual output path from RecordingService (may have been modified during finalization)
        string? actualOutputPath = _recordingService.OutputFilePath ?? _currentRecordingPath;

        // Check if we need to prompt
        if (!_mainVm.UseFixedRecordPath && PickSaveFileAction != null && !string.IsNullOrEmpty(actualOutputPath))
        {
            if (System.IO.File.Exists(actualOutputPath))
            {
                var targetPath = await PickSaveFileAction();
                if (!string.IsNullOrEmpty(targetPath))
                {
                    try
                    {
                        if (System.IO.File.Exists(targetPath)) System.IO.File.Delete(targetPath);
                        System.IO.File.Move(actualOutputPath!, targetPath);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to move recording: {ex.Message}");
                    }
                }
                else
                {
                    // User cancelled, delete temp file
                    try
                    {
                        if (System.IO.File.Exists(actualOutputPath))
                        {
                            System.IO.File.Delete(actualOutputPath);
                            System.Diagnostics.Debug.WriteLine($"Deleted cancelled recording: {actualOutputPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to delete cancelled recording: {ex.Message}");
                    }
                }
            }
        }

        CloseAction?.Invoke();
    }

    private bool _isProcessingRecording = false;

    private async Task CopyRecording()
    {
        if (_isProcessingRecording || _recordingService == null || _mainVm == null) return;
        
        _isProcessingRecording = true;
        try
        {
            _recordTimer?.Stop();
            await _recordingService.StopAsync();
            
            string? actualOutputPath = _recordingService.OutputFilePath ?? _currentRecordingPath;
            
            if (!string.IsNullOrEmpty(actualOutputPath) && !System.IO.File.Exists(actualOutputPath))
            {
               if (!actualOutputPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
               {
                   string withExt = actualOutputPath + ".mkv";
                   if (System.IO.File.Exists(withExt)) actualOutputPath = withExt;
               }
            }

            // Wait loop for existence (up to 2 seconds)
            if (!string.IsNullOrEmpty(actualOutputPath))
            {
                for (int i = 0; i < 20; i++) 
                {
                    if (System.IO.File.Exists(actualOutputPath)) break;
                    await Task.Delay(100);
                }
            }
            
            if (!string.IsNullOrEmpty(actualOutputPath) && System.IO.File.Exists(actualOutputPath))
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = $"-noprofile -command \"Set-Clipboard -Path '{actualOutputPath}'\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    var process = System.Diagnostics.Process.Start(psi);
                    process?.WaitForExit(2000); // Wait up to 2 seconds
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to copy recording to clipboard: {ex.Message}");
                }
            }
            else 
            {
                 System.Diagnostics.Debug.WriteLine($"Video file not found at: {actualOutputPath}");
            }

            CloseAction?.Invoke();
        }
        finally
        {
            _isProcessingRecording = false;
        }
    }

    private async Task PinRecording()
    {
        if (ShowProcessingOverlay || _recordingService == null) return;

        bool wasRecording = _recordingService.State == RecordingState.Recording;

        _isLocalProcessing = true;
        ShowProcessingOverlay = true;
        IsIndeterminate = true;
        ProcessingText = LocalizationService.Instance["FinalizingRecording"] ?? "Finalizing..."; 
        try
        {
            _recordTimer?.Stop();
            await _recordingService.StopAsync();
            
            if (wasRecording)
            {
                  var recordingPath = _recordingService.LastRecordingPath;
                  if (string.IsNullOrEmpty(recordingPath) || !System.IO.File.Exists(recordingPath)) 
                  {
                      System.Diagnostics.Debug.WriteLine($"找不到錄影檔案: {recordingPath}");
                      _isLocalProcessing = false;
                      ShowProcessingOverlay = false;
                      return;
                  }

                 var ffplayPath = _recordingService.Downloader.GetFFplayPath();
                 
                  if (string.IsNullOrEmpty(ffplayPath) || !System.IO.File.Exists(ffplayPath))
                  {
                      System.Diagnostics.Debug.WriteLine($"找不到播放器組件 (ffplay.exe)");
                      _isLocalProcessing = false;
                      ShowProcessingOverlay = false;
                      return;
                  }

                 double scaling = VisualScaling;
                 int x = (int)(SelectionRect.X * scaling) + ScreenOffset.X;
                 int y = (int)(SelectionRect.Y * scaling) + ScreenOffset.Y;
                 
                 int w = (int)(SelectionRect.Width * scaling);
                 int h = (int)(SelectionRect.Height * scaling);
                 double logW = SelectionRect.Width;
                 double logH = SelectionRect.Height;
                 
                 Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                 {
                       var videoVm = new FloatingVideoViewModel(
                           recordingPath, 
                           ffplayPath.Replace("ffplay.exe", "ffmpeg.exe"), 
                           w, h, 
                           logW, logH,
                           SelectionBorderColor, 
                           SelectionBorderThickness,
                           _mainVm?.HideRecordPinDecoration ?? false,
                           _mainVm?.HideRecordPinBorder ?? false,
                           new ClipboardService(),
                           _mainVm?.AppSettingsService);

                  // Set Save Actions
                  videoVm.PickSaveFileAction = PickSaveFileAction;
                  videoVm.SaveAction = async () => await videoVm.SaveCommand.Execute();

                  // Copy annotations from Snip window to the pinned video window
                  var offsetX = SelectionRect.X;
                  var offsetY = SelectionRect.Y;
                  foreach (var ann in Annotations)
                  {
                      var cloned = ann.Clone();
                      cloned.StartPoint = new Point(cloned.StartPoint.X - offsetX, cloned.StartPoint.Y - offsetY);
                      cloned.EndPoint = new Point(cloned.EndPoint.X - offsetX, cloned.EndPoint.Y - offsetY);
                      if (cloned.Points != null && cloned.Points.Count > 0)
                       {
                           var pts_copy = new System.Collections.Generic.List<Avalonia.Point>(cloned.Points);
                           cloned.Points.Clear();
                           foreach(var pt in pts_copy) cloned.Points.Add(new Avalonia.Point(pt.X - offsetX, pt.Y - offsetY));
                       }
                      videoVm.Annotations.Add(cloned);
                  }

                      var pad = videoVm.WindowPadding;
                          
                      var videoWin = new FloatingVideoWindow
                      {
                          DataContext = videoVm,
                          Position = new PixelPoint(x - (int)(pad.Left * scaling), y - (int)(pad.Top * scaling))
                      };
                     
                      videoWin.Show();
                  });
                 
                 CloseAction?.Invoke();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error pinning recording: {ex}");
        }
        finally
        {
            _isLocalProcessing = false;
            ShowProcessingOverlay = false;
        }
    }

    private async Task Copy() 
    { 
        // If recording is processing, ignore copy command to prevent overwriting with screenshot
        if (_isProcessingRecording) return;

        // If recording is active or we are in recording mode with a valid path, use CopyRecording
        if (IsRecordingMode)
        {
             var lastPath = _recordingService?.LastRecordingPath;
             bool hasVideo = !string.IsNullOrEmpty(lastPath) && System.IO.File.Exists(lastPath);
             
             if (RecState == RecordingState.Recording || RecState == RecordingState.Paused || hasVideo)
             {
                 await CopyRecording();
                 return;
             }
        }

        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            HideAction?.Invoke();
            await Task.Delay(200); // Wait for UI update

            try 
            {
                _isLocalProcessing = true;
                ShowProcessingOverlay = true;
                IsIndeterminate = true;
                ProcessingText = LocalizationService.Instance["StatusProcessing"] ?? "Processing...";
                var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, ScreenOffset, VisualScaling, Annotations, UserSelections, TranslatedBlocks, _mainVm?.ShowSnipCursor ?? false);
                await _captureService.CopyToClipboardAsync(bitmap);
                _mainVm?.SetStatus("StatusCopied");
            }
            finally
            {
                _isLocalProcessing = false;
                ShowProcessingOverlay = false;
                CloseAction?.Invoke();
            }
        }
    }

    private async Task Save() 
    { 
         // If recording is active, stop recording instead of saving screenshot
         if (RecState == RecordingState.Recording || RecState == RecordingState.Paused)
         {
             await StopRecording();
             return;
         }

         if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
         {
             HideAction?.Invoke();
             await Task.Delay(200); // Wait for UI update

             try
             {
                 _isLocalProcessing = true;
                 ShowProcessingOverlay = true;
                 IsIndeterminate = true;
                 ProcessingText = LocalizationService.Instance["StatusSaving"] ?? "Saving...";
                 var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, ScreenOffset, VisualScaling, Annotations, UserSelections, TranslatedBlocks, _mainVm?.ShowSnipCursor ?? false);
                 
                 if (_mainVm != null && _mainVm.AutoSave)
                 {
                     var dir = _mainVm.SaveDirectory;
                     if (string.IsNullOrEmpty(dir))
                     {
                         dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), "GimmeCapture");
                     }
                     try { System.IO.Directory.CreateDirectory(dir); } catch { }

                     var fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                     var path = System.IO.Path.Combine(dir, fileName);
                     await _captureService.SaveToFileAsync(bitmap, path);
                     _mainVm?.SetStatus("StatusSaved");
                     System.Diagnostics.Debug.WriteLine($"Auto-saved to {path}");
                 }
                 else if (PickSaveFileAction != null)
                 {
                     var path = await PickSaveFileAction.Invoke();
                     if (!string.IsNullOrEmpty(path))
                     {
                        await _captureService.SaveToFileAsync(bitmap, path);
                        _mainVm?.SetStatus("StatusSaved");
                     }
                     System.Diagnostics.Debug.WriteLine($"Saved to {path}");
                 }
                 else
                 {
                     // Fallback
                     var fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                     var path = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), fileName);
                     await _captureService.SaveToFileAsync(bitmap, path);
                 }
             }
             finally
             {
                 _isLocalProcessing = false;
                 ShowProcessingOverlay = false;
                 CloseAction?.Invoke(); 
             }
         }
    }
    
    private async Task Pin(bool runAI = false)
    {
        System.Diagnostics.Debug.WriteLine($"[SnipWindowViewModel] Pin() called. runAI={runAI}, SelectionRect={SelectionRect}");
        // Guard: If AI is disabled globally, prevent running it
        if (runAI && (_mainVm == null || !_mainVm.EnableAI))
        {
            runAI = false;
        }

        // If recording is active or we are in recording mode with a valid path, use PinRecording
        if (IsRecordingMode)
        {
            var lastPath = _recordingService?.LastRecordingPath;
            bool hasVideo = !string.IsNullOrEmpty(lastPath) && System.IO.File.Exists(lastPath);
            
            if (RecState == RecordingState.Recording || RecState == RecordingState.Paused || hasVideo)
            {
                await PinRecording();
                return;
            }
        }

        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            HideAction?.Invoke();
            await Task.Delay(200); // Wait for UI update
            
            try
            {
                var skBitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, ScreenOffset, VisualScaling, Annotations, UserSelections, TranslatedBlocks, _mainVm?.ShowSnipCursor ?? false);
                
                // Convert SKBitmap to Avalonia Bitmap
                using var image = SkiaSharp.SKImage.FromBitmap(skBitmap);
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var stream = new System.IO.MemoryStream();
                data.SaveTo(stream);
                stream.Position = 0;
                
                var avaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                
                // Open Floating Window
                OpenPinWindowAction?.Invoke(avaloniaBitmap, SelectionRect, SelectionBorderColor, SelectionBorderThickness, runAI);
            }
            finally
            {
                CloseAction?.Invoke();
            }
        }
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
        if (IsTranslationMode) return;

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
