using System;
using System.Diagnostics;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Services.Core.Media;

public partial class RecordingService
{
    private LibavGdigrabMkvSession? _nativeRecorder;

    private async Task<bool> StartFfmpegSegmentAsync(string segmentFile)
    {
        if (!FFmpegRuntime.IsInitialized)
        {
            Debug.WriteLine("[RecordingService] FFmpeg native runtime not initialized.");
            LastStartError = "FFmpeg native runtime not initialized.";
            return false;
        }

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
            LastStartWarning = _nativeRecorder.LastWarningMessage ?? string.Empty;
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
        if (_nativeRecorder == null)
        {
            StopAudioCapture();
            StopMicCapture();
            return;
        }

        try
        {
            await _nativeRecorder.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error stopping native recorder: {ex.Message}");
        }
        finally
        {
            _nativeRecorder.Dispose();
            _nativeRecorder = null;
            StopAudioCapture();
            StopMicCapture();
        }
    }
}
