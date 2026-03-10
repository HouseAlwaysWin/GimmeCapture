using System;
using System.Diagnostics;

namespace GimmeCapture.Services.Core.Media;

public partial class RecordingService
{
    private bool TryStartAudioCapture(string audioSegmentPath)
    {
        try
        {
            StopAudioCapture();
            _audioCapture = new NAudio.Wave.WasapiLoopbackCapture();
            _audioWriter = new NAudio.Wave.WaveFileWriter(audioSegmentPath, _audioCapture.WaveFormat);
            _audioCapture.DataAvailable += (_, e) =>
            {
                TryWriteAudioBuffer(e.Buffer, e.BytesRecorded);
            };
            _audioCapture.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start loopback capture: {ex.Message}");
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

    private void TryWriteAudioBuffer(byte[] buffer, int bytesRecorded)
    {
        try
        {
            _audioWriter?.Write(buffer, 0, bytesRecorded);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Write loopback audio buffer failed: {ex.Message}");
        }
    }
}
