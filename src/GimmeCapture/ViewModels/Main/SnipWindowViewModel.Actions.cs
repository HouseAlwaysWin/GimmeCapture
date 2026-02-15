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
            
            // Update border color based on mode
            SelectionBorderColor = _mainVm?.BorderColor ?? Colors.Red;
            
            this.RaisePropertyChanged(nameof(HideFrameBorder));
            this.RaisePropertyChanged(nameof(HideSelectionDecoration));
            this.RaisePropertyChanged(nameof(ModeDisplayName));
        }
    }

    public string ModeDisplayName => IsRecordingMode 
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
        set => this.RaiseAndSetIfChanged(ref _isRecordingFinalizing, value);
    }

    // Action Helpers
    public bool HideSelectionDecoration => IsRecordingMode ? (_mainVm?.HideRecordSelectionDecoration ?? false) : (_mainVm?.HideSnipSelectionDecoration ?? false);
    public bool HideFrameBorder => IsRecordingMode ? (_mainVm?.HideRecordSelectionBorder ?? false) : (_mainVm?.HideSnipSelectionBorder ?? false);

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
    public ReactiveCommand<Unit, Unit> StartRecordingCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> PauseRecordingCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> StopRecordingCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> CopyRecordingCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> HandleF1Command { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> HandleF2Command { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> RemoveBackgroundCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleTopmostCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleMaskCommand { get; set; } = null!;

    // Init Method
    private void InitializeActionCommands()
    {
        CopyCommand = ReactiveCommand.CreateFromTask(Copy);
        CopyCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        SaveCommand = ReactiveCommand.CreateFromTask(Save);
        SaveCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        PinCommand = ReactiveCommand.CreateFromTask(() => Pin(false));
        PinCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        CloseCommand = ReactiveCommand.Create(Close);
        CloseCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        ToggleModeCommand = ReactiveCommand.Create(() => 
        {
            if (RecState == RecordingState.Idle) IsRecordingMode = !IsRecordingMode;
        });
        ToggleModeCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        SetCaptureModeCommand = ReactiveCommand.Create<bool>(isRecord => 
        {
            if (RecState == RecordingState.Idle) IsRecordingMode = isRecord;
        });
        SetCaptureModeCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        StartRecordingCommand = ReactiveCommand.CreateFromTask(StartRecording);
        StartRecordingCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        PauseRecordingCommand = ReactiveCommand.CreateFromTask(PauseRecording);
        PauseRecordingCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        StopRecordingCommand = ReactiveCommand.CreateFromTask(StopRecording);
        StopRecordingCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        CopyRecordingCommand = ReactiveCommand.CreateFromTask(CopyRecording);
        CopyRecordingCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        HandleF1Command = ReactiveCommand.Create(() => { if (RecState == RecordingState.Idle) IsRecordingMode = false; });
        HandleF2Command = ReactiveCommand.Create(() => 
        { 
            if (RecState == RecordingState.Idle) 
            {
                // USER REQUEST: F2 always switches/sets Record Mode, never auto-starts recording
                if (!IsRecordingMode)
                {
                    IsRecordingMode = true;
                }
            }
        });

        // Action key (F3) logic re-definition for specific command
        PinCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (!IsRecordingMode) await Pin(false);
            else 
            {
                if (RecState == RecordingState.Idle)
                {
                     // If we have a selection, start recording
                     if (CurrentState == SnipState.Selected)
                     {
                         await StartRecording();
                     }
                }
                else 
                {
                    await PinRecording(); // Handles Stop and Pin
                }
            }
        });
        
        // Copy key (Ctrl+C) logic re-definition
        var canCopyImage = this.WhenAnyValue(x => x.IsInputFocused, x => !x);
        CopyCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (!IsRecordingMode) await Copy();
            else await CopyRecording(); // Handles Stop and Copy
        }, canCopyImage);

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

        // If recording is active, copy recording instead of screenshot
        if (RecState == RecordingState.Recording || RecState == RecordingState.Paused)
        {
            await CopyRecording();
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

        // If recording is active, pin recording instead of screenshot
        if (RecState == RecordingState.Recording || RecState == RecordingState.Paused)
        {
            await PinRecording();
            return;
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
