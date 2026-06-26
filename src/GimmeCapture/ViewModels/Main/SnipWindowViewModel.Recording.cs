using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using ReactiveUI;
using GimmeCapture.Services.Core.Infrastructure;
using System;
using System.Threading.Tasks;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    /// <summary>
    /// Logical rect passed to <see cref="RecordingService.StartAsync"/>. Usually equals <see cref="SelectionRect"/> when
    /// <see cref="RecordingUsesWindowsExcludeFromCapture"/> is true (WDA_EXCLUDEFROMCAPTURE). Otherwise may be inset when annotations prevent exclusion.
    /// </summary>
    private Rect? _recordingCaptureLogicalRect;

    /// <summary>
    /// When true, SnipWindow uses Windows exclude-from-capture so FFmpeg records the full <see cref="SelectionRect"/> without overlay chrome (no smaller output size).
    /// False when <see cref="Annotations"/> is non-empty (ink must stay visible to capture) or not on Windows — then <see cref="GetRecordingCaptureRegionExcludingVisibleChrome"/> is used.
    /// </summary>
    private bool _recordingUsesWindowsExcludeFromCapture;
    private bool _recordStartInFlight;
    private DateTime _lastRecordStartAttemptUtc = DateTime.MinValue;

    public bool RecordingUsesWindowsExcludeFromCapture => _recordingUsesWindowsExcludeFromCapture;

    /// <summary>
    /// Fallback when WDA cannot be used: crop gdigrab region inside the selection so border/corners are not in the file (output size is smaller than the selection).
    /// </summary>
    private Rect GetRecordingCaptureRegionExcludingVisibleChrome()
    {
        var r = SelectionRect;
        if (_mainVm == null) return r;

        bool hideBorder = _mainVm.HideRecordSelectionBorder;
        bool hideDeco = _mainVm.HideRecordSelectionDecoration;
        if (hideBorder && hideDeco)
            return r;

        const double cornerMarginPad = 1.5;
        const double edgePad = 2.0;

        double inset = 0;
        if (!hideBorder)
            inset = Math.Max(inset, SelectionBorderThickness * 2 + 1);
        if (!hideDeco)
            inset = Math.Max(inset, SelectionIconSize + SelectionBorderThickness + cornerMarginPad + edgePad);

        if (inset <= 0)
            return r;

        const double minRemain = 8.0;
        if (r.Width <= 2 * inset + minRemain || r.Height <= 2 * inset + minRemain)
            return r;

        return new Rect(r.X + inset, r.Y + inset, r.Width - 2 * inset, r.Height - 2 * inset);
    }

    private void ResetRecordingDurationTracking()
    {
        _recordingAccumulatedDuration = TimeSpan.Zero;
        _recordingActiveStartUtc = DateTime.UtcNow;
        _lastRecordingState = RecordingState.Idle;
        RecordingDuration = TimeSpan.Zero;
    }

    private void EnsureRecordingTimerStarted()
    {
        if (_recordTimer == null)
        {
            _recordTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _recordTimer.Tick += (_, _) => UpdateRecordingDurationFromClock();
        }

        if (!_recordTimer.IsEnabled)
        {
            _recordTimer.Start();
        }
    }

    private void HandleRecordingStateChanged(RecordingState newState)
    {
        if (newState == RecordingState.Idle)
        {
            if (_recordingUsesWindowsExcludeFromCapture)
            {
                _recordingUsesWindowsExcludeFromCapture = false;
                this.RaisePropertyChanged(nameof(RecordingUsesWindowsExcludeFromCapture));
            }
            SyncRecordingScreenCaptureAffinity?.Invoke();
        }

        var nowUtc = DateTime.UtcNow;

        // Close the active recording segment when leaving Recording state.
        if (_lastRecordingState == RecordingState.Recording && newState != RecordingState.Recording)
        {
            var delta = nowUtc - _recordingActiveStartUtc;
            if (delta > TimeSpan.Zero)
            {
                _recordingAccumulatedDuration += delta;
            }
        }

        // Open a new active segment when entering Recording state.
        if (_lastRecordingState != RecordingState.Recording && newState == RecordingState.Recording)
        {
            _recordingActiveStartUtc = nowUtc;
            EnsureRecordingTimerStarted();
        }

        if (newState == RecordingState.Idle && _recordTimer?.IsEnabled == true)
        {
            _recordTimer.Stop();
        }

        _lastRecordingState = newState;
        UpdateRecordingDurationFromClock();
    }

    private void UpdateRecordingDurationFromClock()
    {
        TimeSpan duration = _recordingAccumulatedDuration;

        if (RecState == RecordingState.Recording)
        {
            var live = DateTime.UtcNow - _recordingActiveStartUtc;
            if (live > TimeSpan.Zero)
            {
                duration += live;
            }

            // Size limit check
            if (_mainVm != null && _recordingService != null)
            {
                double maxMb = _mainVm.RecordingSettings.MaxRecordingSizeMB;
                if (maxMb > 0)
                {
                    long currentBytes = _recordingService.GetCurrentRecordingSizeBytes();  
                    if (currentBytes > maxMb * 1024 * 1024)
                    {
                        System.Diagnostics.Debug.WriteLine($"Recording size limit reached: {currentBytes / 1024.0 / 1024.0:F2} MB > {maxMb} MB. Pinning...");
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            ExecutePinRecordingAsync().Forget("Recording.ExecutePin"));
                    }
                }
            }
        }

        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        RecordingDuration = duration;
    }

    private async Task ExecuteStartRecordingAsync()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastRecordStartAttemptUtc) < TimeSpan.FromMilliseconds(1200))
        {
            return;
        }
        _lastRecordStartAttemptUtc = now;

        if (_recordStartInFlight)
        {
            return;
        }

        _recordStartInFlight = true;
        try
        {
            if (_recordingService == null || _mainVm == null)
            {
                return;
            }

            // Validate before showing the countdown so the user does not wait
            // only to discover that recording cannot start.
            if (!_mainVm.FfmpegDownloader.IsFFmpegAvailable())
            {
                _mainVm.SetStatus("FFmpegNotReady");
                return;
            }

            string format = _mainVm.RecordingSettings.RecordFormat?.ToLowerInvariant() ?? "mp4";
            if (format == "gif" && !RecordingFormatCapabilities.IsGifAvailable())
            {
                _mainVm.SetStatus("GifUnavailableReason");
                return;
            }

            var launchResult = await _mainVm.RunCaptureActionAsync(
                CaptureMode.Record,
                () => StartRecordingCoreAsync(format));
            if (launchResult != CaptureLaunchResult.Launched)
            {
                ShowAction?.Invoke();
                FocusWindowAction?.Invoke();
            }
        }
        finally
        {
            _recordStartInFlight = false;
        }
    }

    private async Task StartRecordingCoreAsync(string format)
    {
        // Cancel any pending AI scans immediately
        _scanCts?.Cancel();
        _isLocalProcessing = false;
        ShowProcessingOverlay = false;
        ProcessingText = string.Empty;

        if (_recordingService == null || _mainVm == null) return;

        // Use TempFolder setting if available, otherwise local Temp folder in app directory
        string tempDir = _mainVm.TempDirectory;
        if (string.IsNullOrEmpty(tempDir))
        {
            tempDir = System.IO.Path.Combine(_mainVm.AppSettingsService.BaseDataDirectory, "Temp");
        }

        FileLocationService.EnsureDirectory(tempDir, "SnipRecording.EnsureTempDirectory");

        if (_mainVm.RecordingSettings.UseFixedRecordPath && !string.IsNullOrEmpty(_mainVm.RecordingSettings.VideoSaveDirectory))
        {
            // Ensure directory exists
            FileLocationService.EnsureDirectory(_mainVm.RecordingSettings.VideoSaveDirectory, "SnipRecording.EnsureVideoDirectory");
            string fileName = CaptureFileNameService.BuildFileName(format);
            _currentRecordingPath = System.IO.Path.Combine(_mainVm.RecordingSettings.VideoSaveDirectory, fileName);
        }
        else
        {
            _currentRecordingPath = System.IO.Path.Combine(tempDir, $"GimmeCapture_{Guid.NewGuid()}.{format}");
        }

        bool useWindowsExcludeFromCapture = OperatingSystem.IsWindows() && Annotations.Count == 0;
        _recordingUsesWindowsExcludeFromCapture = useWindowsExcludeFromCapture;
        this.RaisePropertyChanged(nameof(RecordingUsesWindowsExcludeFromCapture));
        SyncRecordingScreenCaptureAffinity?.Invoke();

        _recordingCaptureLogicalRect = null;
        var region = useWindowsExcludeFromCapture
            ? SelectionRect
            : GetRecordingCaptureRegionExcludingVisibleChrome();

        // Ensure size is even for ffmpeg
        if (region.Width % 2 != 0) region = region.WithWidth(region.Width - 1);
        if (region.Height % 2 != 0) region = region.WithHeight(region.Height - 1);

        ResetRecordingDurationTracking();
        bool enableSystemAudio = _mainVm.RecordSystemAudio;

        // Windows picked from the capture-scope picker record via WGC (follows the windows): 1 window =
        // single capture, 2+ = composite/separate per RecordMultiWindowMode. Region/monitor pass an empty
        // list and stay on the gdigrab desktop-region path.
        var windowHandles = new System.Collections.Generic.List<IntPtr>(_recordWindowHandles);

        if (await _recordingService.StartAsync(region, _currentRecordingPath, format, _mainVm.ShowRecordCursor, ScreenOffset, VisualScaling, _mainVm.RecordingSettings.RecordFPS, enableSystemAudio, _mainVm.RecordMicrophone, _mainVm.SelectedMicDeviceId, _mainVm.MicVolume, windowHandle: default, windowHandles: windowHandles, multiWindowMode: RecordMultiWindowMode))
        {
            _recordingCaptureLogicalRect = region;
            EnsureRecordingTimerStarted();
            this.RaisePropertyChanged(nameof(IsMicrophoneEnabled)); // reveal the mic meter for this recording
            // StartAsync resets the mute flags on the service; reflect that on the toolbar toggles for the new session.
            this.RaisePropertyChanged(nameof(IsSystemAudioMuted));
            this.RaisePropertyChanged(nameof(IsMicMuted));
            if (!string.IsNullOrWhiteSpace(_recordingService.LastStartWarning))
            {
                _mainVm.SetStatus(_recordingService.LastStartWarning);
            }
        }
        else
        {
            _mainVm.SetStatus(string.IsNullOrWhiteSpace(_recordingService.LastStartError)
                ? "Recording start failed. Please try again."
                : _recordingService.LastStartError);
            _recordingUsesWindowsExcludeFromCapture = false;
            this.RaisePropertyChanged(nameof(RecordingUsesWindowsExcludeFromCapture));
            SyncRecordingScreenCaptureAffinity?.Invoke();
        }
    }

    private async Task ExecutePauseRecordingAsync()
    {
        if (_recordingService == null) return;
        if (RecState == RecordingState.Recording) await _recordingService.PauseAsync();
        else if (RecState == RecordingState.Paused) await _recordingService.ResumeAsync();
    }

    private async Task ExecuteStopRecordingAsync()
    {
        if (_recordingService == null || _mainVm == null) return;

        _recordTimer?.Stop();
        await _recordingService.StopAsync();

        // Use the actual output path from RecordingService (may have been modified during finalization)
        string? actualOutputPath = await ResolveRecordingFilePathAsync(_recordingService.OutputFilePath ?? _currentRecordingPath);
        string? revealPath = null;

        // Check if we need to prompt
        if (!_mainVm.RecordingSettings.UseFixedRecordPath && PickSaveFileAction != null)
        {
            var targetPath = await PickSaveFileAction();
            if (!string.IsNullOrEmpty(targetPath))
            {
                if (!string.IsNullOrEmpty(actualOutputPath) && System.IO.File.Exists(actualOutputPath))
                {
                    try
                    {
                        if (System.IO.File.Exists(targetPath)) System.IO.File.Delete(targetPath);
                        System.IO.File.Move(actualOutputPath, targetPath);
                        revealPath = targetPath;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to move recording: {ex.Message}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Recording output not found, save dialog had target but source missing: {actualOutputPath}");
                }
            }
            else
            {
                // User cancelled, delete temp file
                try
                {
                    if (!string.IsNullOrEmpty(actualOutputPath) && System.IO.File.Exists(actualOutputPath))
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
        else if (!string.IsNullOrEmpty(actualOutputPath) && System.IO.File.Exists(actualOutputPath))
        {
            revealPath = actualOutputPath;
        }

        if (!string.IsNullOrEmpty(revealPath))
        {
            var baseRect = _recordingCaptureLogicalRect ?? SelectionRect;
            int pw = Math.Max(2, (int)Math.Round(baseRect.Width > 1 ? baseRect.Width : 640.0));
            int ph = Math.Max(2, (int)Math.Round(baseRect.Height > 1 ? baseRect.Height : 360.0));
            _mainVm?.CaptureHistory.AddVideoAsync(revealPath, pw, ph).Forget("CaptureHistory.AddVideo");
            if (_mainVm?.RevealAfterSave ?? true)
            {
                FileLocationService.RevealInFileExplorer(revealPath);
            }
        }

        CloseAction?.Invoke();
    }

    private async Task ExecuteCopyRecordingAsync()
    {
        if (_isProcessingRecording || _recordingService == null || _mainVm == null) return;

        _isProcessingRecording = true;
        try
        {
            _recordTimer?.Stop();
            await _recordingService.StopAsync();

            string? actualOutputPath = await ResolveRecordingFilePathAsync(_recordingService.OutputFilePath ?? _currentRecordingPath);

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

    private async Task ExecutePinRecordingAsync()
    {
        if (ShowProcessingOverlay || _recordingService == null) return;

        bool hasRecordingContext = _recordingService.State != RecordingState.Idle
                                   || !string.IsNullOrEmpty(_currentRecordingPath);

        _isLocalProcessing = true;
        ShowProcessingOverlay = true;
        IsIndeterminate = true;
        ProcessingText = LocalizationService.Instance["FinalizingRecording"] ?? "Finalizing...";
        try
        {
            _recordTimer?.Stop();
            await _recordingService.StopAsync();

            if (hasRecordingContext)
            {
                string? recordingPath = await ResolveRecordingFilePathAsync(
                    _recordingService.OutputFilePath ?? _currentRecordingPath);

                System.Diagnostics.Debug.WriteLine($"[Pin] OutputFilePath={_recordingService.OutputFilePath}");
                System.Diagnostics.Debug.WriteLine($"[Pin] LastRecordingPath={_recordingService.LastRecordingPath}");
                System.Diagnostics.Debug.WriteLine($"[Pin] _currentRecordingPath={_currentRecordingPath}");
                System.Diagnostics.Debug.WriteLine($"[Pin] Resolved recordingPath={recordingPath}");
                System.Diagnostics.Debug.WriteLine($"[Pin] File.Exists={(!string.IsNullOrEmpty(recordingPath) && System.IO.File.Exists(recordingPath))}");
                if (!string.IsNullOrEmpty(recordingPath) && System.IO.File.Exists(recordingPath))
                    System.Diagnostics.Debug.WriteLine($"[Pin] FileSize={new System.IO.FileInfo(recordingPath).Length}");

                if (string.IsNullOrEmpty(recordingPath) || !System.IO.File.Exists(recordingPath))
                {
                    System.Diagnostics.Debug.WriteLine($"找不到錄影檔案: {recordingPath}");
                    return;
                }

                var baseRect = _recordingCaptureLogicalRect ?? SelectionRect;
                var originalWidth = Math.Max(2.0, baseRect.Width > 1 ? baseRect.Width : 640.0);
                var originalHeight = Math.Max(2.0, baseRect.Height > 1 ? baseRect.Height : 360.0);
                int pixelWidth = Math.Max(2, (int)Math.Round(originalWidth));
                int pixelHeight = Math.Max(2, (int)Math.Round(originalHeight));
                bool hideDecoration = _mainVm?.HideRecordPinDecoration ?? false;
                bool hideBorder = _mainVm?.HideRecordPinBorder ?? false;

                if (OpenPinnedVideoWindowAction != null)
                {
                    try
                    {
                        OpenPinnedVideoWindowAction(
                            recordingPath,
                            pixelWidth,
                            pixelHeight,
                            originalWidth,
                            originalHeight,
                            SelectionBorderColor,
                            SelectionBorderThickness,
                            hideDecoration,
                            hideBorder);
                    }
                    catch
                    {
                        FileLocationService.RevealInFileExplorer(recordingPath);
                    }
                }
                else
                {
                    FileLocationService.RevealInFileExplorer(recordingPath);
                }

                _mainVm?.CaptureHistory.AddVideoAsync(recordingPath, pixelWidth, pixelHeight).Forget("CaptureHistory.AddVideo");

                _recordingService.ClearLastRecording();
                _currentRecordingPath = null;
                _recordingCaptureLogicalRect = null;
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

    private async Task<string?> ResolveRecordingFilePathAsync(string? preferredPath)
    {
        if (string.IsNullOrWhiteSpace(preferredPath)) return preferredPath;

        string path = preferredPath;

        for (int i = 0; i < 20; i++)
        {
            if (System.IO.File.Exists(path)) return path;
            await Task.Delay(100);
        }

        // Try common extension fallbacks if final output extension changed.
        string basePath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path) ?? string.Empty,
            System.IO.Path.GetFileNameWithoutExtension(path));

        string[] candidates = [".mp4", ".mkv", ".avi", ".webm", ".mov", ".gif"];
        foreach (var ext in candidates)
        {
            var alt = basePath + ext;
            if (System.IO.File.Exists(alt)) return alt;
        }

        // Keep original path for diagnostics if still not found.
        return path;
    }

}
