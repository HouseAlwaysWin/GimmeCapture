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
    private bool _isRecordingMode;
    public bool IsRecordingMode
    {
        get => _isRecordingMode;
        set 
        {
            this.RaiseAndSetIfChanged(ref _isRecordingMode, value);
            
            // Exiting recording mode should also exit translation mode
            if (value) IsTranslationMode = false;
            
            // Update border color (Unified Theme Color)
            SelectionBorderColor = _mainVm?.ThemeColor ?? Colors.Yellow;
            
            this.RaisePropertyChanged(nameof(HideFrameBorder));
            this.RaisePropertyChanged(nameof(HideSelectionDecoration));
            this.RaisePropertyChanged(nameof(ModeDisplayName));
            this.RaisePropertyChanged(nameof(IsScreenshotMode));
        }
    }

    private bool _isTranslationMode;
    public bool IsTranslationMode
    {
        get => _isTranslationMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isTranslationMode, value);
            System.Diagnostics.Debug.WriteLine($"[Mode] IsTranslationMode -> {value}");
            
            if (value)
            {
                // 進入翻譯模式：啟用遮罩並更新挖空區域、退出錄影模式
                _isRecordingMode = false;
                this.RaisePropertyChanged(nameof(IsRecordingMode));
                SelectionRect = new Rect(0, 0, 0, 0); // 確保清空標準選取框，避免干擾挖空
                IsMaskVisible = true;
                this.RaisePropertyChanged(nameof(MaskOpacity));
                UpdateMask();
                
                StartAutoDetectLoop();
            }
            else
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

            // Update border color (Unified Theme Color)
            SelectionBorderColor = _mainVm?.ThemeColor ?? Colors.Yellow;
            
            this.RaisePropertyChanged(nameof(IsTranslationMode));
            this.RaisePropertyChanged(nameof(HideSelectionDecoration));
            this.RaisePropertyChanged(nameof(IsScreenshotMode));
            this.RaisePropertyChanged(nameof(IsToolbarVisible));
            this.RaisePropertyChanged(nameof(ModeDisplayName));
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
    /// 截圖模式（非錄影且非翻譯）
    /// </summary>
    public bool IsScreenshotMode => !IsRecordingMode && !IsTranslationMode;

    /// <summary>
    /// 工具列是否可見：翻譯模式始終可見，其他模式需要在 Selected 狀態且未 Finalizing
    /// </summary>
    public bool IsToolbarVisible => ShowToolbar && (IsTranslationMode || (CurrentState == SnipState.Selected && !IsRecordingFinalizing));

    public string ModeDisplayName => IsTranslationMode 
        ? LocalizationService.Instance["CaptureModeTranslation"] ?? "Translation"
        : IsRecordingMode 
            ? LocalizationService.Instance["CaptureModeRecord"] 
            : LocalizationService.Instance["CaptureModeNormal"];

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
            // Force hide decoration during active recording or paused so they aren't captured
            if (IsRecordingMode && RecState != RecordingState.Idle) return true;

            // Always show decoration during selection phase
            if (IsRecordingMode && RecState == RecordingState.Idle) return false;
            
            return IsRecordingMode ? (_mainVm?.HideRecordSelectionDecoration ?? false) : (_mainVm?.HideSnipSelectionDecoration ?? false);
        }
    }

    public bool HideFrameBorder 
    {
        get
        {
            // You can optionally force hide border here if needed, but we respect the setting
            // Always show border during selection phase so user can see what they are selecting
            if (IsRecordingMode && RecState == RecordingState.Idle) return false;
            return IsRecordingMode ? (_mainVm?.HideRecordSelectionBorder ?? false) : (_mainVm?.HideSnipSelectionBorder ?? false);
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
             if (!IsRecordingMode) IsRecordingMode = true;
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

    public void HandleGlobalHotkey(int modeInt)
    {
        if (_mainVm == null) return;
        
        var mode = (MainWindowViewModel.CaptureMode)modeInt;

        string pressedHotkey = mode switch {
            MainWindowViewModel.CaptureMode.Normal => _mainVm.SnipHotkey,
            MainWindowViewModel.CaptureMode.Record => _mainVm.RecordHotkey,
            MainWindowViewModel.CaptureMode.Translate => _mainVm.TranslateHotkey,
            MainWindowViewModel.CaptureMode.Copy => _mainVm.CopyHotkey,
            _ => ""
        };

        if (!string.IsNullOrEmpty(pressedHotkey) && pressedHotkey == _mainVm.PinHotkey)
        {
             if (HandleF3Command != null)
             {
                 HandleF3Command.Execute().Subscribe();
             }
             return;
        }

        if (mode == MainWindowViewModel.CaptureMode.Normal)
        {
            if (HandleF1Command != null) HandleF1Command.Execute().Subscribe();
        }
        else if (mode == MainWindowViewModel.CaptureMode.Record)
        {
            if (HandleF2Command != null) HandleF2Command.Execute().Subscribe();
        }
        else if (mode == MainWindowViewModel.CaptureMode.Translate)
        {
            if (SetTranslationModeCommand != null) SetTranslationModeCommand.Execute().Subscribe();
        }
        else if (mode == MainWindowViewModel.CaptureMode.Copy)
        {
            AutoActionMode = 1;
            if (CurrentState == SnipState.Selected) TriggerAutoAction(); 
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
            if (RecState == RecordingState.Idle) IsRecordingMode = !IsRecordingMode;
        }, canExecuteHotkeys);
        ToggleModeCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        SetCaptureModeCommand = ReactiveCommand.Create<bool>(isRecord => 
        {
            if (RecState == RecordingState.Idle)
            {
                IsTranslationMode = false;
                IsRecordingMode = isRecord;
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
                IsRecordingMode = false;
                IsTranslationMode = false;
            }
        }, canExecuteHotkeys);
        HandleF2Command = ReactiveCommand.Create(() => 
        { 
            if (RecState == RecordingState.Idle) 
            {
                // USER REQUEST: F2 always switches/sets Record Mode, never auto-starts recording
                if (!IsRecordingMode)
                {
                    IsTranslationMode = false;
                    IsRecordingMode = true;
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
            if (IsTranslationMode)
            {
                // 翻譯沒有Pin: F3 -> 空
                return;
            }
            
            if (PinCommand != null)
            {
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
                    IsTranslationMode = false;
                }
                else
                {
                    // 進入翻譯模式
                    IsTranslationMode = true;
                    IsRecordingMode = false;
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
        
        // Ensure size is even for ffmpeg
        if (region.Width % 2 != 0) region = region.WithWidth(region.Width - 1);
        if (region.Height % 2 != 0) region = region.WithHeight(region.Height - 1);

        if (await _recordingService.StartAsync(SelectionRect, _currentRecordingPath, _mainVm.RecordFormat ?? "mp4", _mainVm.ShowRecordCursor, ScreenOffset, VisualScaling, _mainVm.RecordFPS))
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
                var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, ScreenOffset, VisualScaling, Annotations, _mainVm?.ShowSnipCursor ?? false);
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
                 var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, ScreenOffset, VisualScaling, Annotations, _mainVm?.ShowSnipCursor ?? false);
                 
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
                var skBitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, ScreenOffset, VisualScaling, Annotations, _mainVm?.ShowSnipCursor ?? false);
                
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
