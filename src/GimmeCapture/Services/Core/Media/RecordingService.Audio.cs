using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
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

    // Each Windows capture's bursts through _audioGate, for as long as the capture runs.
    private RecordingAudioGate.Feed? _systemAudioFeed;
    private RecordingAudioGate.Feed? _micFeed;

    // How long a pause or stop waits for the captures' audio up to it. They hand audio over every 50-70 ms, so anything
    // longer means the device has gone.
    private static readonly TimeSpan AudioTailTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Test seam: tests have no audio device, so they hand in a stand-in endpoint and check what is opened on it and in
    /// which order. Production never calls this.
    /// </summary>
    internal void UseSystemAudioEndpointForTest(Func<ISystemAudioEndpoint> open) => _openSystemAudioEndpoint = open;

    private bool TryStartLinuxPulse(string wavPath, string source, ref LinuxPulseAudioRecorder? slot, Func<bool> isMuted)
    {
        slot?.Dispose();
        slot = null;
        var rec = new LinuxPulseAudioRecorder(source, wavPath, isMuted, () => _audioGate.IsOpen);
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
            var feed = _systemAudioFeed = _audioGate.NewFeed();
            _audioCapture.DataAvailable += (_, e) =>
            {
                TryWriteAudioBuffer(feed, Stopwatch.GetTimestamp(), e.Buffer, e.BytesRecorded, captureFormat, writerFormat);
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

    /// <param name="videoLength">The segment's video length, to end its audio at (<see cref="PadToVideoLength"/>).</param>
    internal void StopAudioCapture(TimeSpan? videoLength = null)
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
            DisposeAudioResources(videoLength);
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
            var feed = _micFeed = _audioGate.NewFeed();
            _micCapture.DataAvailable += (_, e) =>
            {
                TryWriteMicBuffer(feed, Stopwatch.GetTimestamp(), e.Buffer, e.BytesRecorded, captureFormat, writerFormat);
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

    private void StopMicCapture(TimeSpan? videoLength = null)
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
            try
            {
                PadToVideoLength(_micWriter, videoLength, _micFeed);
            }
            catch (Exception ex)
            {
                AppLog.Warning("Recording.MicPad", ex);
            }

            try { _micWriter?.Dispose(); } catch { /* best effort */ }
            try { _micDevice?.Dispose(); } catch { /* best effort */ }
            _micCapture = null;
            _micWriter = null;
            _micDevice = null;
            _micFeed = null;
        }
    }

    private void TryWriteMicBuffer(RecordingAudioGate.Feed feed, long arrival, byte[] buffer, int bytesRecorded, WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        try
        {
            WriteInsideGate(feed, arrival, _micWriter, MuteMicrophone, buffer, bytesRecorded, sourceFormat, targetFormat);
        }
        catch (Exception ex)
        {
            AppLog.Debug("RecordingService.Audio", $"Write mic audio buffer failed: {ex.Message}");
            LogToFile($"[Audio] Write mic buffer failed: {ex.Message}");
        }
    }

    private void DisposeAudioResources(TimeSpan? videoLength = null)
    {
        Exception? firstException = null;

        TryExecute("Dispose loopback capture", () => _audioCapture?.Dispose());
        TryExecute("Pad to the video's length", () => PadToVideoLength(_audioWriter, videoLength, _systemAudioFeed));
        TryExecute("Dispose wave writer", () => _audioWriter?.Dispose());
        // Closed after the capture it kept delivering. A failed start comes through here too, so none is left open.
        TryExecute("Stop silent keep-alive stream", () => _systemAudioKeepAlive?.Dispose());
        TryExecute("Release system audio endpoint", () => _systemAudioEndpoint?.Dispose());

        _audioCapture = null;
        _audioWriter = null;
        _systemAudioKeepAlive = null;
        _systemAudioEndpoint = null;
        _systemAudioFeed = null;

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

    private void TryWriteAudioBuffer(RecordingAudioGate.Feed feed, long arrival, byte[] buffer, int bytesRecorded, WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        try
        {
            WriteInsideGate(feed, arrival, _audioWriter, MuteSystemAudio, buffer, bytesRecorded, sourceFormat, targetFormat);
        }
        catch (Exception ex)
        {
            AppLog.Debug("RecordingService.Audio", $"Write loopback audio buffer failed: {ex.Message}");
            LogToFile($"[Audio] Write buffer failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the part of a captured burst the gate lets through, as 16-bit PCM. Audio outside the video's capture is
    /// dropped, not written as silence the way a mute is: there is no picture for it, and keeping its length is exactly
    /// what would put the tracks out of step.
    /// </summary>
    internal static void WriteInsideGate(RecordingAudioGate.Feed feed, long arrival, WaveFileWriter? writer, bool muted, byte[] buffer, int bytesRecorded, WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        int blockAlign = sourceFormat.BlockAlign;
        var (first, end) = feed.Admit(arrival, bytesRecorded / blockAlign, sourceFormat.SampleRate);
        if (writer == null || end <= first)
        {
            return;
        }

        int offset = first * blockAlign;
        int count = (end - first) * blockAlign;
        if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat && sourceFormat.BitsPerSample == 32 && targetFormat.BitsPerSample == 16)
        {
            int sampleCount = count / 4;
            byte[] pcm16 = new byte[sampleCount * 2];
            // Muted: leave pcm16 as zeros (silence) so the WAV stays the same length and A/V stays in sync.
            if (!muted)
            {
                int outIndex = 0;
                for (int i = 0; i < sampleCount; i++)
                {
                    float sample = BitConverter.ToSingle(buffer, offset + i * 4);
                    sample = Math.Clamp(sample, -1f, 1f);
                    short s16 = (short)Math.Round(sample * short.MaxValue);
                    pcm16[outIndex++] = (byte)(s16 & 0xFF);
                    pcm16[outIndex++] = (byte)((s16 >> 8) & 0xFF);
                }
            }
            writer.Write(pcm16, 0, pcm16.Length);
        }
        else if (muted)
        {
            writer.Write(new byte[count], 0, count);
        }
        else
        {
            writer.Write(buffer, offset, count);
        }
    }

    /// <summary>
    /// Ends a paused or stopped segment's audio: waits until each capture has handed over its audio up to the moment the
    /// gate closed, stops the captures, ends their audio where the video ends, and logs all of it as
    /// <c>Recording.AudioGate</c>. A capture hands audio over only at its next poll, up to 70 ms on, and stopping it
    /// before then lost the end of the segment. The video's own stop has usually taken 30-60 ms of that wait already.
    /// </summary>
    private async Task StopAudioAtVideoEndAsync(TimeSpan? videoLength)
    {
        var system = _systemAudioFeed;
        var mic = _micFeed;
        long started = Stopwatch.GetTimestamp();
        if (system != null || mic != null)
        {
            var reached = Task.WhenAll(system?.ReachedClose ?? Task.CompletedTask, mic?.ReachedClose ?? Task.CompletedTask);
            await Task.WhenAny(reached, Task.Delay(AudioTailTimeout)).ConfigureAwait(false);
        }

        TimeSpan waited = Stopwatch.GetElapsedTime(started);
        StopAudioCapture(videoLength);
        StopMicCapture(videoLength);
        if (system == null && mic == null)
        {
            LastAudioGate = null;
            return;
        }

        var line = new StringBuilder();
        if (system != null)
        {
            line.Append("system ").Append(system.Describe()).Append(' ');
        }

        if (mic != null)
        {
            line.Append("mic ").Append(mic.Describe()).Append(' ');
        }

        line.Append("waited=").Append(waited.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)).Append("ms");
        LastAudioGate = line.ToString();
        AppLog.Information("Recording.AudioGate", LastAudioGate);
    }

    /// <summary>
    /// Ends a segment's audio where its video ends, with silence. After the stop the video still takes the frame it was
    /// capturing, and its last frame lasts one more: it ran 45-105 ms past the stop, measured. Audio that ends at the stop
    /// comes out that much short, and finalize, which joins the segments' audio end to end, then played every later
    /// segment's that much early. Silence, as that stretch is after the user stopped. Called with the capture stopped,
    /// whose thread was the writer's only other user.
    /// </summary>
    internal static void PadToVideoLength(WaveFileWriter? writer, TimeSpan? videoLength, RecordingAudioGate.Feed? feed)
    {
        if (writer == null || videoLength is not { } length)
        {
            return;
        }

        var format = writer.WaveFormat;
        long missing = (long)Math.Round(length.TotalSeconds * format.SampleRate) - writer.Length / format.BlockAlign;
        feed?.NotePad(missing, format.SampleRate);
        if (missing <= 0)
        {
            return;
        }

        var silence = new byte[(int)Math.Min(missing, format.SampleRate) * format.BlockAlign];
        for (long left = missing; left > 0;)
        {
            int frames = (int)Math.Min(left, format.SampleRate);
            writer.Write(silence, 0, frames * format.BlockAlign);
            left -= frames;
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
