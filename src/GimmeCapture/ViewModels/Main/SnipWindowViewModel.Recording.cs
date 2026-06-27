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
    // Set once when the disk-space guard auto-stops a recording, so the 200ms poll doesn't re-trigger.
    private bool _lowDiskStopTriggered;
    // Stop the recording if free space on the temp drive drops below this (leaves headroom for finalize).
    private const long MinFreeDiskBytes = 500L * 1024 * 1024;
    private DateTime _lastRecordStartAttemptUtc = DateTime.MinValue;

    // Folder holding the per-window files when recording multiple windows to separate files; null otherwise.
    private string? _separateRecordingFolder;

    // Per-output logical bounds for separate-window recording, captured at START (index-aligned with the output
    // files). Snapshotted because the pin path runs after ClearRecordingSelectionVisuals() empties
    // _recordWindowHandles (via SelectionRect=default), so SeparateOutputBoundsForIndex can no longer look them up.
    private System.Collections.Generic.List<Rect>? _separateOutputLogicalBounds;

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
        _lowDiskStopTriggered = false;
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
        // The selection border/decoration are hidden while recording (HideFrameBorder/HideSelectionDecoration
        // gate on RecState) to avoid the un-clearable capture-excluded DWM ghost — re-evaluate them on every change.
        this.RaisePropertyChanged(nameof(HideFrameBorder));
        this.RaisePropertyChanged(nameof(HideSelectionDecoration));

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

                // Disk-space guard: stop and keep what's captured before the drive fills up mid-write.
                if (!_lowDiskStopTriggered && _recordingService.GetTempDriveAvailableBytes() < MinFreeDiskBytes)
                {
                    _lowDiskStopTriggered = true;
                    System.Diagnostics.Debug.WriteLine("Low disk space — stopping recording to preserve the capture.");
                    _mainVm.SetStatus("RecordLowDiskSpace");
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        ExecutePinRecordingAsync().Forget("Recording.LowDiskStop"));
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

            // The record toolbar is always visible in record mode (fixed top-center), so guard against
            // starting with nothing chosen: need picked window(s) or a non-empty drawn/monitor region.
            bool hasTarget = _recordWindowHandles.Count > 0
                || (SelectionRect.Width >= 2 && SelectionRect.Height >= 2);
            if (!hasTarget)
            {
                _mainVm.SetStatus("RecordSelectTargetFirst");
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

        // Separate mode: build one output path per window in a timestamped folder we can reveal afterwards.
        _separateRecordingFolder = null;
        _separateOutputLogicalBounds = null;
        System.Collections.Generic.List<string>? separateOutputFiles = null;
        if (windowHandles.Count >= 2 && RecordMultiWindowMode == MultiWindowMode.Separate)
        {
            separateOutputFiles = BuildSeparateOutputPaths(windowHandles, format);

            // Snapshot each window's logical bounds now, index-aligned with windowHandles/the output files. The
            // pin path needs these to size each pinned window correctly, but by then _recordWindowHandles is
            // cleared, so we can't look them up there — capture them here while the selection is still valid.
            var boundsSnapshot = new System.Collections.Generic.List<Rect>(windowHandles.Count);
            foreach (var hwnd in windowHandles)
            {
                Rect b = SelectionRect;
                foreach (var target in CaptureTargets)
                {
                    if (!target.IsMonitor && target.Hwnd == hwnd)
                    {
                        b = target.LogicalBounds;
                        break;
                    }
                }
                boundsSnapshot.Add(b);
            }
            _separateOutputLogicalBounds = boundsSnapshot;
        }

        if (await _recordingService.StartAsync(region, _currentRecordingPath, format, _mainVm.ShowRecordCursor, ScreenOffset, VisualScaling, _mainVm.RecordingSettings.RecordFPS, enableSystemAudio, _mainVm.RecordMicrophone, _mainVm.SelectedMicDeviceId, _mainVm.MicVolume, windowHandle: default, windowHandles: windowHandles, multiWindowMode: RecordMultiWindowMode, separateOutputFiles: separateOutputFiles))
        {
            _recordingCaptureLogicalRect = region;
            EnsureRecordingTimerStarted();
            this.RaisePropertyChanged(nameof(IsMicrophoneEnabled)); // reveal the mic meter for this recording
            // StartAsync resets the mute flags on the service; reflect that on the toolbar toggles for the new session.
            this.RaisePropertyChanged(nameof(IsSystemAudioMuted));
            this.RaisePropertyChanged(nameof(IsMicMuted));
            // Suppressed: on a machine where WGC can't deliver frames the region fallback IS the normal path, so
            // surfacing "… capture unavailable, recording the region instead" on every recording is just noise.
            // (The fallback still works; the limitation is logged.) Real start *failures* are still shown below.
            if (!string.IsNullOrWhiteSpace(_recordingService.LastStartWarning))
            {
                AppLog.Information($"Recording.StartWarning (suppressed in UI): {_recordingService.LastStartWarning}");
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

    /// <summary>
    /// Clears the on-screen selection visuals left by a recording: the yellow selection-border window region
    /// (drawn while <c>CurrentState == Selected</c> with a non-empty <see cref="SelectionRect"/>) and the
    /// window snap-candidate / AI-scan outlines. Without this the yellow frame stays drawn over the recorded
    /// window after stop/pin if the overlay isn't torn down promptly (dual-monitor repro). Emptying
    /// <see cref="SelectionRect"/> makes the region subscription re-run <c>UpdateWindowRegion</c>, which drops
    /// the border ring. Safe because pin/save sizing uses <c>_recordingCaptureLogicalRect</c>, captured at
    /// record start, not <see cref="SelectionRect"/>. See docs/WGC_HANDOFF.md.
    /// </summary>
    private void ClearRecordingSelectionVisuals()
    {
        AppLog.Information($"Recording.ClearVisuals v2 called: selBefore={SelectionRect} state={CurrentState} recState={RecState}");

        if (WindowRects.Count > 0)
        {
            WindowRects.Clear();
        }

        if (AIScanRects.Count > 0)
        {
            AIScanRects.Clear();
        }

        // Empty the selection AND drop out of the Selected/Selecting state. The yellow selection border is a Grid
        // sized to SelectionRect and shown by StateToBoolConverter(CurrentState in {Selecting,Selected}); clearing
        // both guarantees it disappears even if the overlay isn't torn down and even if SelectionRect is re-set.
        SelectionRect = default;
        CurrentState = SnipState.Idle;

        // Belt-and-suspenders: directly collapse the Win32 border region now (the binding that does this is
        // throttled and may be disposed by an immediate Close), so the yellow ring can't linger as a stale frame.
        ForceClearSelectionRegionAction?.Invoke();

        // The yellow frame after recording was a SEPARATE leftover overlay window (a stale snip overlay or a
        // scrolling-capture region outline), not this window's own border — close any such leftovers now.
        CloseStaleOverlayWindowsAction?.Invoke();

        AppLog.Information($"Recording.ClearVisuals v2 done: selAfter={SelectionRect} state={CurrentState}");

        // DIAGNOSTIC: 2s after stop (overlay has closed by then), dump this process's top-level windows so we can
        // see what — if anything — still draws the lingering yellow recording frame (a real window vs a DWM ghost).
        System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(2000).ConfigureAwait(false);
            GimmeCapture.Services.Interop.Win32Helpers.LogTopLevelWindowsOfCurrentProcess("2s-after-stop");
            // Re-run the overlay scan/close on the UI thread now that any late-appearing leftover frame is present.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => CloseStaleOverlayWindowsAction?.Invoke());
        }).Forget("Recording.WinDiag");
    }

    private async Task ExecuteStopRecordingAsync()
    {
        if (_recordingService == null || _mainVm == null) return;

        _recordTimer?.Stop();
        // Clear the selection border BEFORE StopAsync: finalize hides the capture-excluded overlay, and hiding it
        // while the yellow border is still drawn leaves that border as a DWM ghost. Clearing it first prevents that.
        ClearRecordingSelectionVisuals();
        await _recordingService.StopAsync();

        // Separate multi-window recording produced N files; save them all and skip the single-file flow.
        if (TryHandleSeparateOutputs(pin: false))
        {
            CloseAction?.Invoke();
            return;
        }

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

    /// <summary>
    /// If the just-stopped recording produced multiple per-window files (separate mode), handles them all
    /// and returns true so the single-file finish flow is skipped. When <paramref name="pin"/> is set each
    /// file is opened as its own floating pin window; otherwise the files are added to history and their
    /// folder is revealed. (Every file is always added to history.)
    /// </summary>
    private bool TryHandleSeparateOutputs(bool pin)
    {
        if (_recordingService == null)
        {
            return false;
        }

        var outputs = _recordingService.SeparateOutputFiles;
        if (outputs.Count == 0)
        {
            return false;
        }

        bool hideDecoration = _mainVm?.HideRecordPinDecoration ?? false;
        bool hideBorder = _mainVm?.HideRecordPinBorder ?? false;

        for (int i = 0; i < outputs.Count; i++)
        {
            string file = outputs[i];
            if (string.IsNullOrEmpty(file) || !System.IO.File.Exists(file))
            {
                continue;
            }

            var bounds = SeparateOutputBoundsForIndex(i);
            double ow = Math.Max(2.0, bounds.Width > 1 ? bounds.Width : 640.0);
            double oh = Math.Max(2.0, bounds.Height > 1 ? bounds.Height : 360.0);
            int pw = Math.Max(2, (int)Math.Round(ow));
            int ph = Math.Max(2, (int)Math.Round(oh));

            if (pin && OpenPinnedVideoWindowAction != null)
            {
                try
                {
                    OpenPinnedVideoWindowAction(file, pw, ph, ow, oh, SelectionBorderColor, SelectionBorderThickness, hideDecoration, hideBorder);
                }
                catch
                {
                    FileLocationService.RevealInFileExplorer(file);
                }
            }

            _mainVm?.CaptureHistory.AddVideoAsync(file, pw, ph).Forget("CaptureHistory.AddVideo");
        }

        if (!pin && (_mainVm?.RevealAfterSave ?? true) && !string.IsNullOrEmpty(_separateRecordingFolder))
        {
            FileLocationService.RevealInFileExplorer(_separateRecordingFolder!);
        }

        _recordingService.ClearLastRecording();
        return true;
    }

    /// <summary>Best-effort logical bounds for the i-th separate output (the picked window's bounds, else the selection).</summary>
    private Rect SeparateOutputBoundsForIndex(int index)
    {
        // Prefer the start-time snapshot: by pin time _recordWindowHandles has been cleared (SelectionRect=default
        // in ClearRecordingSelectionVisuals), so the live lookup below would always fall through to the full
        // multi-window bounding box and size every pin to the whole virtual desktop.
        if (_separateOutputLogicalBounds != null && index >= 0 && index < _separateOutputLogicalBounds.Count)
        {
            return _separateOutputLogicalBounds[index];
        }

        if (index >= 0 && index < _recordWindowHandles.Count)
        {
            IntPtr hwnd = _recordWindowHandles[index];
            foreach (var target in CaptureTargets)
            {
                if (!target.IsMonitor && target.Hwnd == hwnd)
                {
                    return target.LogicalBounds;
                }
            }
        }

        return _recordingCaptureLogicalRect ?? SelectionRect;
    }

    private System.Collections.Generic.List<string> BuildSeparateOutputPaths(System.Collections.Generic.List<IntPtr> handles, string format)
    {
        string baseDir = _mainVm != null && !string.IsNullOrEmpty(_mainVm.RecordingSettings.VideoSaveDirectory)
            ? _mainVm.RecordingSettings.VideoSaveDirectory
            : System.IO.Path.Combine(_mainVm?.AppSettingsService.BaseDataDirectory ?? AppContext.BaseDirectory, "Recordings");

        string folder = System.IO.Path.Combine(baseDir, $"GimmeCapture_Windows_{DateTime.Now:yyyyMMdd_HHmmss}");
        FileLocationService.EnsureDirectory(folder, "SnipRecording.EnsureSeparateDir");
        _separateRecordingFolder = folder;

        var paths = new System.Collections.Generic.List<string>(handles.Count);
        for (int i = 0; i < handles.Count; i++)
        {
            string safe = SanitizeForFileName(WindowTitleForHandle(handles[i]));
            if (safe.Length > 40)
            {
                safe = safe.Substring(0, 40).TrimEnd();
            }

            paths.Add(System.IO.Path.Combine(folder, $"{i + 1:D2}_{safe}.{format}"));
        }

        return paths;
    }

    private string WindowTitleForHandle(IntPtr hwnd)
    {
        foreach (var target in CaptureTargets)
        {
            if (!target.IsMonitor && target.Hwnd == hwnd)
            {
                return target.DisplayName;
            }
        }

        return "window";
    }

    private static string SanitizeForFileName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "window";
        }

        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        string sanitized = sb.ToString().Trim();
        return sanitized.Length == 0 ? "window" : sanitized;
    }

    private async Task ExecuteCopyRecordingAsync()
    {
        if (_isProcessingRecording || _recordingService == null || _mainVm == null) return;

        _isProcessingRecording = true;
        try
        {
            _recordTimer?.Stop();
            ClearRecordingSelectionVisuals(); // before StopAsync — see ExecuteStopRecordingAsync (prevents border ghost).
            await _recordingService.StopAsync();

            // Separate mode has N files; copying N to the clipboard isn't meaningful, so save + reveal them.
            if (TryHandleSeparateOutputs(pin: false))
            {
                CloseAction?.Invoke();
                return;
            }

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
            ClearRecordingSelectionVisuals(); // before StopAsync — see ExecuteStopRecordingAsync (prevents border ghost).
            await _recordingService.StopAsync();

            // Separate mode: pin each window's file as its own floating window.
            if (TryHandleSeparateOutputs(pin: true))
            {
                CloseAction?.Invoke();
                return;
            }

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
                    // Still close the snip overlay — otherwise a failed pin leaves the selection on screen with
                    // no way to dismiss it (reported on the dual-monitor repro). See docs/WGC_HANDOFF.md.
                    CloseAction?.Invoke();
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
