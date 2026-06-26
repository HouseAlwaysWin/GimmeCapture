using System;
using System.Diagnostics;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.Services.Platforms.Windows;

namespace GimmeCapture.Services.Core.Media;

public partial class RecordingService
{
    private LibavGdigrabMkvSession? _nativeRecorder;
    private LibavWgcMkvSession? _nativeWgcRecorder;

    private async Task<bool> StartFfmpegSegmentAsync(string segmentFile)
    {
        if (!FFmpegRuntime.IsInitialized)
        {
            Debug.WriteLine("[RecordingService] FFmpeg native runtime not initialized.");
            LastStartError = "FFmpeg native runtime not initialized.";
            return false;
        }

        // A picked window records via Windows Graphics Capture so the output follows the window. If WGC
        // can't start (no support / window gone), fall back to the gdigrab region so recording still works.
        if (_windowHandle != IntPtr.Zero
            && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
            && WgcWindowCaptureSource.IsSupported)
        {
            if (await StartWgcSegmentAsync(segmentFile).ConfigureAwait(false))
            {
                return true;
            }

            Debug.WriteLine($"[RecordingService] WGC window capture failed; falling back to region. {LastStartError}");
            LastStartWarning = "Window capture unavailable — recording the window's current screen region instead.";
        }

        return await StartGdigrabSegmentAsync(segmentFile).ConfigureAwait(false);
    }

    private async Task<bool> StartWgcSegmentAsync(string segmentFile)
    {
        try
        {
            _nativeWgcRecorder?.Dispose();
            _nativeWgcRecorder = new LibavWgcMkvSession
            {
                PreferHardwareEncoder = _settingsService?.Settings.VideoEncoderHint != VideoEncoderHint.SoftwareOnly
            };

            bool useH265 = _settingsService?.Settings.VideoCodec == VideoCodec.H265;

            var ok = await _nativeWgcRecorder.StartAsync(segmentFile, _windowHandle, _fps, _includeCursor, useH265)
                .ConfigureAwait(false);
            if (!ok)
            {
                LastStartError = _nativeWgcRecorder.LastErrorMessage ?? "WGC window session reported failure.";
                _nativeWgcRecorder.Dispose();
                _nativeWgcRecorder = null;
                return false;
            }

            LastStartWarning = _nativeWgcRecorder.LastWarningMessage ?? string.Empty;
            _lastSelectedVideoEncoderName = _nativeWgcRecorder.SelectedEncoderName ?? string.Empty;
            State = RecordingState.Recording;
            return true;
        }
        catch (Exception ex)
        {
            LastStartError = ex.Message;
            Debug.WriteLine($"[RecordingService] WGC recorder start failed: {ex.Message}");
            _nativeWgcRecorder?.Dispose();
            _nativeWgcRecorder = null;
            return false;
        }
    }

    private async Task<bool> StartGdigrabSegmentAsync(string segmentFile)
    {
        try
        {
            _nativeRecorder?.Dispose();
            _nativeRecorder = new LibavGdigrabMkvSession
            {
                EnableWebcam = _settingsService?.Settings.EnableWebcam ?? false,
                WebcamDeviceName = _settingsService?.Settings.WebcamDeviceName ?? string.Empty,
                WebcamCorner = _settingsService?.Settings.WebcamCorner ?? 3,
                HighlightCursor = _settingsService?.Settings.HighlightCursor ?? false,
                HighlightClicks = _settingsService?.Settings.HighlightClicks ?? false,
                PreferHardwareEncoder = _settingsService?.Settings.VideoEncoderHint != VideoEncoderHint.SoftwareOnly
            };

            int x = (int)(_region.X * _visualScaling) + _screenOffset.X;
            int y = (int)(_region.Y * _visualScaling) + _screenOffset.Y;
            int w = ((int)(_region.Width * _visualScaling) / 2) * 2;
            int h = ((int)(_region.Height * _visualScaling) / 2) * 2;

            bool useH265 = _settingsService?.Settings.VideoCodec == VideoCodec.H265;

            var ok = await _nativeRecorder.StartAsync(segmentFile, x, y, w, h, _fps, _includeCursor, useH265)
                .ConfigureAwait(false);
            LastStartWarning = string.IsNullOrEmpty(LastStartWarning)
                ? _nativeRecorder.LastWarningMessage ?? string.Empty
                : LastStartWarning;
            _lastSelectedVideoEncoderName = _nativeRecorder.SelectedEncoderName ?? string.Empty;
            if (!ok)
            {
                LastStartError = _nativeRecorder.LastErrorMessage ?? "Native gdigrab session reported failure.";
                Debug.WriteLine($"[RecordingService] Native gdigrab session reported failure: {LastStartError}");
                _nativeRecorder.Dispose();
                _nativeRecorder = null;
                State = RecordingState.Idle;
                return false;
            }

            State = RecordingState.Recording;
            return true;
        }
        catch (Exception ex)
        {
            LastStartError = ex.Message;
            Debug.WriteLine($"[RecordingService] Native recorder start failed: {ex.Message}");
            _nativeRecorder?.Dispose();
            _nativeRecorder = null;
            State = RecordingState.Idle;
            return false;
        }
    }

    private async Task StopCurrentSegmentAsync()
    {
        if (_nativeRecorder == null && _nativeWgcRecorder == null)
        {
            StopAudioCapture();
            StopMicCapture();
            return;
        }

        try
        {
            if (_nativeWgcRecorder != null)
            {
                await _nativeWgcRecorder.StopAsync().ConfigureAwait(false);
            }

            if (_nativeRecorder != null)
            {
                await _nativeRecorder.StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping native recorder: {ex.Message}");
        }
        finally
        {
            _nativeWgcRecorder?.Dispose();
            _nativeWgcRecorder = null;
            _nativeRecorder?.Dispose();
            _nativeRecorder = null;
            StopAudioCapture();
            StopMicCapture();
        }
    }
}
