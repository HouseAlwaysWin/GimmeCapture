using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Views.Floating;
using System;
using System.Threading.Tasks;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    private async Task ExecuteStartRecordingAsync()
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
            string fileName = CaptureFileNameService.BuildFileName(format);
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

        if (await _recordingService.StartAsync(region, _currentRecordingPath, _mainVm!.RecordFormat ?? "mp4", _mainVm.ShowRecordCursor, ScreenOffset, VisualScaling, _mainVm.RecordFPS, _mainVm.RecordSystemAudio))
        {
            RecordingDuration = TimeSpan.Zero;

            _recordTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _recordTimer.Tick += (s, e) =>
            {
                if (RecState == RecordingState.Recording)
                    RecordingDuration = RecordingDuration.Add(TimeSpan.FromSeconds(1));
            };
            _recordTimer.Start();
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
        if (!_mainVm.UseFixedRecordPath && PickSaveFileAction != null)
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
            FileLocationService.RevealInFileExplorer(revealPath);
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
                                   || !string.IsNullOrEmpty(_recordingService.LastRecordingPath)
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
                string? recordingPath = await ResolveRecordingFilePathAsync(_recordingService.OutputFilePath ?? _recordingService.LastRecordingPath ?? _currentRecordingPath);

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

                var ffplayPath = _recordingService.Downloader.GetFFplayPath();

                if (string.IsNullOrEmpty(ffplayPath) || !System.IO.File.Exists(ffplayPath))
                {
                    System.Diagnostics.Debug.WriteLine($"找不到播放器組件 (ffplay.exe)");
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
                    videoVm.SaveAction = () =>
                    {
                        videoVm.SaveCommand.Execute().Subscribe();
                        return Task.CompletedTask;
                    };

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
                            var ptsCopy = new System.Collections.Generic.List<Avalonia.Point>(cloned.Points);
                            cloned.Points.Clear();
                            foreach (var pt in ptsCopy) cloned.Points.Add(new Avalonia.Point(pt.X - offsetX, pt.Y - offsetY));
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

        string[] candidates = [".mp4", ".mkv", ".webm", ".mov", ".gif"];
        foreach (var ext in candidates)
        {
            var alt = basePath + ext;
            if (System.IO.File.Exists(alt)) return alt;
        }

        // Keep original path for diagnostics if still not found.
        return path;
    }

}
