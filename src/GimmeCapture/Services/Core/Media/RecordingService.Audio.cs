using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GimmeCapture.Services.Core.Media;

public partial class RecordingService
{
    private bool TryStartAudioCapture(string audioSegmentPath)
    {
        try
        {
            StopAudioCapture();
            _audioCapture = new WasapiLoopbackCapture();
            var captureFormat = _audioCapture.WaveFormat;
            var writerFormat = new WaveFormat(captureFormat.SampleRate, 16, captureFormat.Channels);
            _audioWriter = new WaveFileWriter(audioSegmentPath, writerFormat);
            LogToFile($"[Audio] Start loopback capture: {audioSegmentPath}, captureFormat={captureFormat}, writerFormat={writerFormat}");
            _audioCapture.DataAvailable += (_, e) =>
            {
                TryWriteAudioBuffer(e.Buffer, e.BytesRecorded, captureFormat, writerFormat);
            };
            _audioCapture.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start loopback capture: {ex.Message}");
            LogToFile($"[Audio] Start loopback capture failed: {ex.Message}");
            StopAudioCapture();
            return false;
        }
    }

    private void StopAudioCapture()
    {
        try
        {
            _audioCapture?.StopRecording();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Stop loopback capture failed: {ex.Message}");
            LogToFile($"[Audio] Stop loopback capture failed: {ex.Message}");
        }
        finally
        {
            DisposeAudioResources();
        }
    }

    private bool TryStartMicCapture(string micSegmentPath)
    {
        try
        {
            StopMicCapture();

            _micDevice = ResolveMicDevice(_micDeviceId);
            MMDevice? device = _micDevice;
            _micCapture = device != null ? new WasapiCapture(device) : new WasapiCapture();

            var captureFormat = _micCapture.WaveFormat;
            var writerFormat = new WaveFormat(captureFormat.SampleRate, 16, captureFormat.Channels);
            _micWriter = new WaveFileWriter(micSegmentPath, writerFormat);
            LogToFile($"[Audio] Start mic capture: {micSegmentPath}, device={device?.FriendlyName ?? "default"}, captureFormat={captureFormat}");
            _micCapture.DataAvailable += (_, e) =>
            {
                TryWriteMicBuffer(e.Buffer, e.BytesRecorded, captureFormat, writerFormat);
            };
            _micCapture.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start mic capture: {ex.Message}");
            LogToFile($"[Audio] Start mic capture failed: {ex.Message}");
            StopMicCapture();
            return false;
        }
    }

    private static MMDevice? ResolveMicDevice(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    if (string.Equals(d.ID, deviceId, StringComparison.Ordinal))
                    {
                        return d;
                    }
                    d.Dispose();
                }
            }

            // Empty / unknown id → default capture endpoint.
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Resolve mic device failed: {ex.Message}");
            return null;
        }
    }

    private void StopMicCapture()
    {
        try
        {
            _micCapture?.StopRecording();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Stop mic capture failed: {ex.Message}");
            LogToFile($"[Audio] Stop mic capture failed: {ex.Message}");
        }
        finally
        {
            try { _micCapture?.Dispose(); } catch { /* best effort */ }
            try { _micWriter?.Dispose(); } catch { /* best effort */ }
            try { _micDevice?.Dispose(); } catch { /* best effort */ }
            _micCapture = null;
            _micWriter = null;
            _micDevice = null;
        }
    }

    private void TryWriteMicBuffer(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        try
        {
            if (_micWriter == null || bytesRecorded <= 0)
            {
                return;
            }

            if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat && sourceFormat.BitsPerSample == 32 && targetFormat.BitsPerSample == 16)
            {
                int sampleCount = bytesRecorded / 4;
                byte[] pcm16 = new byte[sampleCount * 2];
                int outIndex = 0;
                for (int i = 0; i < sampleCount; i++)
                {
                    float sample = BitConverter.ToSingle(buffer, i * 4);
                    sample = Math.Clamp(sample, -1f, 1f);
                    short s16 = (short)Math.Round(sample * short.MaxValue);
                    pcm16[outIndex++] = (byte)(s16 & 0xFF);
                    pcm16[outIndex++] = (byte)((s16 >> 8) & 0xFF);
                }
                _micWriter.Write(pcm16, 0, pcm16.Length);
            }
            else
            {
                _micWriter.Write(buffer, 0, bytesRecorded);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Write mic audio buffer failed: {ex.Message}");
            LogToFile($"[Audio] Write mic buffer failed: {ex.Message}");
        }
    }

    private void DisposeAudioResources()
    {
        Exception? firstException = null;

        TryExecute("Dispose loopback capture", () => _audioCapture?.Dispose());
        TryExecute("Dispose wave writer", () => _audioWriter?.Dispose());

        _audioCapture = null;
        _audioWriter = null;

        if (firstException != null)
        {
            Debug.WriteLine($"Audio cleanup completed with errors: {firstException.Message}");
            LogToFile($"[Audio] Cleanup completed with errors: {firstException.Message}");
        }
        else
        {
            LogToFile("[Audio] Cleanup completed.");
        }

        void TryExecute(string action, Action operation)
        {
            try
            {
                operation();
            }
            catch (Exception ex)
            {
                firstException ??= ex;
                Debug.WriteLine($"{action} failed: {ex.Message}");
            }
        }
    }

    private void TryWriteAudioBuffer(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        try
        {
            if (_audioWriter == null || bytesRecorded <= 0)
            {
                return;
            }

            if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat && sourceFormat.BitsPerSample == 32 && targetFormat.BitsPerSample == 16)
            {
                int sampleCount = bytesRecorded / 4;
                byte[] pcm16 = new byte[sampleCount * 2];
                int outIndex = 0;
                for (int i = 0; i < sampleCount; i++)
                {
                    float sample = BitConverter.ToSingle(buffer, i * 4);
                    sample = Math.Clamp(sample, -1f, 1f);
                    short s16 = (short)Math.Round(sample * short.MaxValue);
                    pcm16[outIndex++] = (byte)(s16 & 0xFF);
                    pcm16[outIndex++] = (byte)((s16 >> 8) & 0xFF);
                }
                _audioWriter.Write(pcm16, 0, pcm16.Length);
            }
            else
            {
                _audioWriter.Write(buffer, 0, bytesRecorded);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Write loopback audio buffer failed: {ex.Message}");
            LogToFile($"[Audio] Write buffer failed: {ex.Message}");
        }
    }
}
