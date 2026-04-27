using System;
using System.Diagnostics;
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
