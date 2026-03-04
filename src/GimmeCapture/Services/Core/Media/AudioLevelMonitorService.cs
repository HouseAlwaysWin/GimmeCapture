using System;
using NAudio.CoreAudioApi;

namespace GimmeCapture.Services.Core.Media;

public sealed class AudioLevelMonitorService : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _renderDevice;
    private MMDevice? _captureDevice;

    public double OutputPeak { get; private set; }
    public double InputPeak { get; private set; }

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
        _captureDevice ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
    }

    private void ResetDevices()
    {
        try { _renderDevice?.Dispose(); } catch { }
        try { _captureDevice?.Dispose(); } catch { }
        try { _enumerator?.Dispose(); } catch { }

        _renderDevice = null;
        _captureDevice = null;
        _enumerator = null;
    }

    public void Dispose()
    {
        ResetDevices();
    }
}
