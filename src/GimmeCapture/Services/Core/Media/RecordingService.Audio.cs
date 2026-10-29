using System;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Services.Core.Media;

public partial class RecordingService
{
    // Linux uses libav PulseAudio capture instead of NAudio WASAPI (Windows-only).
    private LinuxPulseAudioRecorder? _linuxSystemAudio;
    private LinuxPulseAudioRecorder? _linuxMic;

    // WASAPI loopback hands over nothing at all while no stream renders to its endpoint: no packets, not even silent
    // ones, and nothing fills that stretch in from timestamps. A stretch where nothing played went missing from the
    // system-audio track, so every sound after it played early against the picture. A stream of our own rendering
    // zeros to the same endpoint, open for as long as the capture, keeps the packets coming (OBS does the same).
    private ISystemAudioEndpoint? _systemAudioEndpoint;
    private IDisposable? _systemAudioKeepAlive;
    private Func<ISystemAudioEndpoint> _openSystemAudioEndpoint = WasapiSystemAudioEndpoint.OpenDefault;

    /// <summary>
    /// Test seam: tests have no audio device, so they hand in a stand-in endpoint and check what is opened on it and in
    /// which order. Production never calls this.
    /// </summary>
    internal void UseSystemAudioEndpointForTest(Func<ISystemAudioEndpoint> open) => _openSystemAudioEndpoint = open;

    private bool TryStartLinuxPulse(string wavPath, string source, ref LinuxPulseAudioRecorder? slot, Func<bool> isMuted)
    {
        slot?.Dispose();
        slot = null;
        var rec = new LinuxPulseAudioRecorder(source, wavPath, isMuted, () => _audioGateOpen);
        if (rec.Start())
        {
            slot = rec;
            return true;
        }

        rec.Dispose();
        return false;
    }

    private static void StopLinuxPulse(ref LinuxPulseAudioRecorder? slot)
    {
        slot?.Dispose();
        slot = null;
    }

    internal bool TryStartAudioCapture(string audioSegmentPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return TryStartLinuxPulse(audioSegmentPath, LinuxPulseAudioRecorder.DefaultMonitorSource, ref _linuxSystemAudio, () => MuteSystemAudio);
        }

        try
        {
            StopAudioCapture();
            _systemAudioEndpoint = _openSystemAudioEndpoint();
            _audioCapture = _systemAudioEndpoint.CreateLoopbackCapture();
            var captureFormat = _audioCapture.WaveFormat;
            var writerFormat = new WaveFormat(captureFormat.SampleRate, 16, captureFormat.Channels);
            _audioWriter = new WaveFileWriter(audioSegmentPath, writerFormat);

            // Before the capture starts, so it has a stream to carry from its first packet on. In the capture's format,
            // which is the endpoint's own mix format, so there is nothing to convert.
            _systemAudioKeepAlive = TryStartSilence(_systemAudioEndpoint, captureFormat);
            LogToFile($"[Audio] Start loopback capture: {audioSegmentPath}, captureFormat={captureFormat}, writerFormat={writerFormat}, keepAlive={_systemAudioKeepAlive != null}");
            _audioCapture.DataAvailable += (_, e) =>
            {
                TryWriteAudioBuffer(e.Buffer, e.BytesRecorded, captureFormat, writerFormat);
            };
            _audioCapture.StartRecording();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug("RecordingService.Audio", $"Failed to start loopback capture: {ex.Message}");
            LogToFile($"[Audio] Start loopback capture failed: {ex.Message}");
            StopAudioCapture();
            return false;
        }
    }

    // Best effort: without it the recording goes on as before, and only a stretch where nothing plays can drop out of
    // the system audio. A warning, since that is something the user would hear.
    private static IDisposable? TryStartSilence(ISystemAudioEndpoint endpoint, WaveFormat format)
    {
        try
        {
            return endpoint.StartSilence(format);
        }
        catch (Exception ex)
        {
            AppLog.Warning("Recording.SystemAudioKeepAlive", ex);
            return null;
        }
    }

    internal void StopAudioCapture()
    {
        if (!OperatingSystem.IsWindows())
        {
            StopLinuxPulse(ref _linuxSystemAudio);
            return;
        }

        try
        {
            _audioCapture?.StopRecording();
        }
        catch (Exception ex)
        {
            AppLog.Debug("RecordingService.Audio", $"Stop loopback capture failed: {ex.Message}");
            LogToFile($"[Audio] Stop loopback capture failed: {ex.Message}");
        }
        finally
        {
            DisposeAudioResources();
        }
    }

    private bool TryStartMicCapture(string micSegmentPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return TryStartLinuxPulse(micSegmentPath, LinuxPulseAudioRecorder.DefaultSource, ref _linuxMic, () => MuteMicrophone);
        }

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
            AppLog.Debug("RecordingService.Audio", $"Failed to start mic capture: {ex.Message}");
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
            AppLog.Warning("Recording.ResolveMic", ex);
            return null;
        }
    }

    private void StopMicCapture()
    {
        if (!OperatingSystem.IsWindows())
        {
            StopLinuxPulse(ref _linuxMic);
            return;
        }

        try
        {
            _micCapture?.StopRecording();
        }
        catch (Exception ex)
        {
            AppLog.Debug("RecordingService.Audio", $"Stop mic capture failed: {ex.Message}");
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

            // Outside the video's capture: dropped, not written as silence the way a mute is. There is no picture for
            // this audio, so keeping its length is exactly what would put the tracks out of step.
            if (!_audioGateOpen)
            {
                return;
            }

            if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat && sourceFormat.BitsPerSample == 32 && targetFormat.BitsPerSample == 16)
            {
                int sampleCount = bytesRecorded / 4;
                byte[] pcm16 = new byte[sampleCount * 2];
                // Muted: leave pcm16 as zeros (silence) so the WAV stays the same length and A/V stays in sync.
                if (!MuteMicrophone)
                {
                    int outIndex = 0;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        float sample = BitConverter.ToSingle(buffer, i * 4);
                        sample = Math.Clamp(sample, -1f, 1f);
                        short s16 = (short)Math.Round(sample * short.MaxValue);
                        pcm16[outIndex++] = (byte)(s16 & 0xFF);
                        pcm16[outIndex++] = (byte)((s16 >> 8) & 0xFF);
                    }
                }
                _micWriter.Write(pcm16, 0, pcm16.Length);
            }
            else if (MuteMicrophone)
            {
                _micWriter.Write(new byte[bytesRecorded], 0, bytesRecorded);
            }
            else
            {
                _micWriter.Write(buffer, 0, bytesRecorded);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("RecordingService.Audio", $"Write mic audio buffer failed: {ex.Message}");
            LogToFile($"[Audio] Write mic buffer failed: {ex.Message}");
        }
    }

    private void DisposeAudioResources()
    {
        Exception? firstException = null;

        TryExecute("Dispose loopback capture", () => _audioCapture?.Dispose());
        TryExecute("Dispose wave writer", () => _audioWriter?.Dispose());
        // Closed after the capture it kept delivering. A failed start comes through here too, so none is left open.
        TryExecute("Stop silent keep-alive stream", () => _systemAudioKeepAlive?.Dispose());
        TryExecute("Release system audio endpoint", () => _systemAudioEndpoint?.Dispose());

        _audioCapture = null;
        _audioWriter = null;
        _systemAudioKeepAlive = null;
        _systemAudioEndpoint = null;

        if (firstException != null)
        {
            AppLog.Debug("RecordingService.Audio", $"Audio cleanup completed with errors: {firstException.Message}");
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
                AppLog.Debug("Recording.AudioCleanup", ex); // the first failure is logged below
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

            // Outside the video's capture: dropped, not written as silence the way a mute is. There is no picture for
            // this audio, so keeping its length is exactly what would put the tracks out of step.
            if (!_audioGateOpen)
            {
                return;
            }

            if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat && sourceFormat.BitsPerSample == 32 && targetFormat.BitsPerSample == 16)
            {
                int sampleCount = bytesRecorded / 4;
                byte[] pcm16 = new byte[sampleCount * 2];
                // Muted: leave pcm16 as zeros (silence) so the WAV stays the same length and A/V stays in sync.
                if (!MuteSystemAudio)
                {
                    int outIndex = 0;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        float sample = BitConverter.ToSingle(buffer, i * 4);
                        sample = Math.Clamp(sample, -1f, 1f);
                        short s16 = (short)Math.Round(sample * short.MaxValue);
                        pcm16[outIndex++] = (byte)(s16 & 0xFF);
                        pcm16[outIndex++] = (byte)((s16 >> 8) & 0xFF);
                    }
                }
                _audioWriter.Write(pcm16, 0, pcm16.Length);
            }
            else if (MuteSystemAudio)
            {
                _audioWriter.Write(new byte[bytesRecorded], 0, bytesRecorded);
            }
            else
            {
                _audioWriter.Write(buffer, 0, bytesRecorded);
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("RecordingService.Audio", $"Write loopback audio buffer failed: {ex.Message}");
            LogToFile($"[Audio] Write buffer failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The render endpoint system audio is recorded from: a loopback capture of what it plays, and a stream of silence
    /// of our own that keeps that capture delivering while nothing else plays.
    /// </summary>
    internal interface ISystemAudioEndpoint : IDisposable
    {
        IWaveIn CreateLoopbackCapture();

        /// <summary>
        /// Starts rendering zeros in <paramref name="format"/> to the endpoint. Disposing the result stops it.
        /// </summary>
        IDisposable StartSilence(WaveFormat format);
    }

    /// <summary>
    /// The default render endpoint: the one a <see cref="WasapiLoopbackCapture"/> records when given none.
    /// </summary>
    internal sealed class WasapiSystemAudioEndpoint : ISystemAudioEndpoint
    {
        private readonly MMDevice _device;

        private WasapiSystemAudioEndpoint(MMDevice device) => _device = device;

        // Looked up once for both, so the silent stream plays on the device the capture records even if the default
        // changes in between.
        public static ISystemAudioEndpoint OpenDefault()
        {
            using var enumerator = new MMDeviceEnumerator();
            return new WasapiSystemAudioEndpoint(enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
        }

        public IWaveIn CreateLoopbackCapture() => new WasapiLoopbackCapture(_device);

        public IDisposable StartSilence(WaveFormat format)
        {
            // Shared mode mixes it in with whatever else plays, and zeros add nothing to that mix: nothing is heard,
            // and the capture records what it did before, only without the gaps. Event-driven: closing it takes about
            // one device period (11 ms measured), where polling took 70 ms.
            var output = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, latency: 200);
            try
            {
                output.PlaybackStopped += (_, e) =>
                {
                    // Only a failure carries an exception, such as the device going away mid-recording; a stop asked
                    // for does not.
                    if (e.Exception != null)
                    {
                        AppLog.Warning("Recording.SystemAudioKeepAlive", e.Exception);
                    }
                };
                output.Init(new SilenceProvider(format));
                output.Play();
                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }

        public void Dispose() => _device.Dispose();
    }
}
