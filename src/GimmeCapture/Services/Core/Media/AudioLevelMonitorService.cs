using System;
using NAudio.CoreAudioApi;

namespace GimmeCapture.Services.Core.Media;

public sealed class AudioLevelMonitorService : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _renderDevice;
    private MMDevice? _captureDevice;
    private string? _micDeviceId;

    public double OutputPeak { get; private set; }
    public double InputPeak { get; private set; }

    /// <summary>
    /// Specific capture endpoint to meter (the recording's selected mic). Empty/null = default input.
    /// Changing it re-resolves the device on the next refresh so the input meter follows the chosen mic.
    /// </summary>
    public string? MicDeviceId
    {
        get => _micDeviceId;
        set
        {
            if (string.Equals(_micDeviceId, value, StringComparison.Ordinal))
            {
                return;
            }

            _micDeviceId = value;
            _captureDevice?.Dispose();
            _captureDevice = null;
        }
    }

    public bool TryRefresh()
    {
        try
        {
            EnsureDevices();
            OutputPeak = _renderDevice?.AudioMeterInformation.MasterPeakValue ?? 0;
            InputPeak = _captureDevice?.AudioMeterInformation.MasterPeakValue ?? 0;
            return true;
        }
        catch
        {
            OutputPeak = 0;
            InputPeak = 0;
            ResetDevices();
            return false;
        }
    }

    private void EnsureDevices()
    {
        _enumerator ??= new MMDeviceEnumerator();
        _renderDevice ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _captureDevice ??= ResolveCaptureDevice();
    }

    private MMDevice ResolveCaptureDevice()
    {
        _enumerator ??= new MMDeviceEnumerator();
        if (!string.IsNullOrWhiteSpace(_micDeviceId))
        {
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (string.Equals(d.ID, _micDeviceId, StringComparison.Ordinal))
                {
                    return d;
                }

                d.Dispose();
            }
        }

        return _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
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
