using System;
using NAudio.CoreAudioApi;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Reads the default output's peak level and, when asked, a microphone's, through the devices' own meters. Its COM
/// objects belong to the thread that first reads: <see cref="AudioLevelMeter"/> reads and disposes it on its own.
/// </summary>
public sealed class AudioLevelMonitorService : IAudioLevelReader
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _renderDevice;
    private MMDevice? _captureDevice;
    private string? _micDeviceId;

    /// <summary>
    /// The output's peak, and the microphone's when <paramref name="microphone"/> is set: the endpoint named by
    /// <paramref name="microphoneDeviceId"/> (the recording's selected mic), the default input when it is empty. A
    /// microphone the recording does not take is not opened at all. False, with both 0, when the devices cannot be read.
    /// </summary>
    public bool TryRead(bool microphone, string? microphoneDeviceId, out double output, out double input)
    {
        output = 0;
        input = 0;
        // Core Audio is Windows': elsewhere every reading threw, and was caught, every 120 ms in Record mode.
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            _renderDevice ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            output = _renderDevice.AudioMeterInformation.MasterPeakValue;
            input = 0;
            if (microphone)
            {
                if (!string.Equals(_micDeviceId, microphoneDeviceId, StringComparison.Ordinal))
                {
                    _micDeviceId = microphoneDeviceId;
                    _captureDevice?.Dispose();
                    _captureDevice = null;
                }

                _captureDevice ??= ResolveCaptureDevice();
                input = _captureDevice.AudioMeterInformation.MasterPeakValue;
            }

            return true;
        }
        catch
        {
            output = 0;
            input = 0;
            ResetDevices();
            return false;
        }
    }

    private MMDevice ResolveCaptureDevice()
    {
        if (!string.IsNullOrWhiteSpace(_micDeviceId))
        {
            foreach (var d in _enumerator!.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (string.Equals(d.ID, _micDeviceId, StringComparison.Ordinal))
                {
                    return d;
                }

                d.Dispose();
            }
        }

        return _enumerator!.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
    }

    private void ResetDevices()
    {
        _renderDevice?.Dispose();
        _captureDevice?.Dispose();
        _enumerator?.Dispose();

        _renderDevice = null;
        _captureDevice = null;
        _enumerator = null;
    }

    public void Dispose()
    {
        ResetDevices();
    }
}
