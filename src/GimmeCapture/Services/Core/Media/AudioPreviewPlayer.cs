using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Preview-audio playback shared by the video editors (Pin floating video + compress 進階影片編輯):
/// plays a source file's audio from a start position at an effective speed. 1× non-webm goes through
/// NAudio's <see cref="MediaFoundationReader"/> (native rate); any other speed (or webm) decodes PCM via
/// <see cref="LibavPinAudioPcmDecoder"/> and retimes by adjusting the sample rate. Output prefers
/// WasapiOut and falls back to WaveOutEvent. The component owns the player/stream/decode-token trio and
/// is safe to Stop/Start repeatedly from the host's playback loop.
/// </summary>
internal sealed class AudioPreviewPlayer : IDisposable
{
    private IWavePlayer? _player;
    private WaveStream? _stream;
    private VolumeSampleProvider? _volumeProvider;
    private CancellationTokenSource? _decodeCts;
    private BufferedWaveProvider? _buffered;
    private Task? _decodeTask;
    private volatile bool _disposed;
    private float _volume = 1f;
    private LinuxPulseAudioOutput? _linuxOutput; // Linux audio output (NAudio IWavePlayer is Windows-only)

    /// <summary>
    /// Preview playback volume, 0–1. Scales the audio samples <em>in the stream</em> (via
    /// <see cref="VolumeSampleProvider"/>), so it only affects this preview — it never touches
    /// <see cref="IWavePlayer.Volume"/>, which would move the whole app's level in the Windows mixer
    /// (that bug drove system audio to max on open). Applies live to the current playback.
    /// </summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            var vp = _volumeProvider;
            if (vp != null)
            {
                vp.Volume = _volume;
            }

            var lo = _linuxOutput;
            if (lo != null)
            {
                lo.Volume = _volume;
            }
        }
    }

    /// <summary>
    /// (Re)start audio at <paramref name="startSeconds"/> with the given effective speed. Any previous
    /// playback is stopped first. Failures fall back to the decoded path, then log and stay silent
    /// (preview audio is best-effort).
    /// </summary>
    public void Start(string videoPath, double startSeconds, double effectiveSpeed)
    {
        Stop();
        if (_disposed || string.IsNullOrEmpty(videoPath))
        {
            return;
        }

        double start = Math.Max(0, startSeconds);
        try
        {
            if (ShouldUseDecodedPlayback(videoPath, effectiveSpeed))
            {
                StartDecodedPlayback(videoPath, start, effectiveSpeed);
                return;
            }

            var reader = new MediaFoundationReader(videoPath);
            if (_disposed)
            {
                reader.Dispose();
                return;
            }

            reader.CurrentTime = TimeSpan.FromSeconds(start);
            _stream = reader;
            _player = CreateOutput(_stream);
            _player.Play();
        }
        catch (Exception ex)
        {
            if (_disposed)
            {
                return;
            }

            Debug.WriteLine($"[PinAudio] primary output failed: {ex.Message}");
            Stop();

            try
            {
                StartDecodedPlayback(videoPath, start, effectiveSpeed);
            }
            catch (Exception fallbackEx)
            {
                Debug.WriteLine($"[PinAudio] FFmpeg fallback failed: {fallbackEx.Message}");
                Stop();
            }
        }
    }

    // The MediaFoundationReader path plays at native (1×) speed only; any non-1× effective speed must go
    // through the decoded path, which retimes audio. WebM is not MediaFoundation-decodable.
    private static bool ShouldUseDecodedPlayback(string videoPath, double effectiveSpeed)
    {
        // MediaFoundationReader is Windows-only, so on Linux/macOS always take the libav-decoded path.
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        return string.Equals(Path.GetExtension(videoPath), ".webm", StringComparison.OrdinalIgnoreCase)
            || Math.Abs(effectiveSpeed - 1.0) > 0.01;
    }

    private void StartDecodedPlayback(string videoPath, double startSeconds, double playbackSpeed)
    {
        if (_disposed)
        {
            return;
        }

        _decodeCts = new CancellationTokenSource();
        if (_disposed)
        {
            CancelDecode()?.Dispose();
            return;
        }

        var token = _decodeCts.Token;

        // Stream the audio: start the device on an initially-empty BufferedWaveProvider, then feed it from a
        // background decode. First audio lands in ~100-200 ms; memory stays bounded (decoder throttled ~4 s
        // ahead) regardless of file size — versus the old full-file PCM decode that blocked the play loop and
        // held hundreds of MB in a MemoryStream.
        _decodeTask = Task.Run(() =>
        {
            try
            {
                LibavPinAudioPcmDecoder.DecodeStreaming(
                    videoPath,
                    startSeconds,
                    onFormat: sourceFormat =>
                    {
                        if (_disposed || token.IsCancellationRequested)
                        {
                            return;
                        }

                        // Retime by declaring the buffer at sourceRate*speed and feeding it source-rate S16
                        // PCM — same pitch-shift retiming as the old RawSourceWaveStream path.
                        WaveFormat retimed = CreatePlaybackWaveFormat(sourceFormat, playbackSpeed);

                        if (!OperatingSystem.IsWindows())
                        {
                            // NAudio's device output is Windows-only; play the decoded PCM through PulseAudio.
                            // pa_simple_write blocks, so it provides the backpressure the BufferedWaveProvider
                            // gave on Windows — waitIfFull below stays a no-op (there's no _buffered on Linux).
                            var output = new LinuxPulseAudioOutput { Volume = _volume };
                            if (!output.Start(retimed.SampleRate, retimed.Channels) || _disposed || token.IsCancellationRequested)
                            {
                                output.Dispose();
                                return;
                            }

                            _linuxOutput = output;
                            return;
                        }

                        var buffered = new BufferedWaveProvider(retimed)
                        {
                            BufferDuration = TimeSpan.FromSeconds(4),
                            DiscardOnBufferOverflow = false,
                            ReadFully = true, // underrun => silence, never signals end-of-stream (device won't stop)
                        };

                        IWavePlayer player = CreateOutput(buffered);
                        if (_disposed || token.IsCancellationRequested)
                        {
                            player.Dispose();
                            return;
                        }

                        _buffered = buffered;
                        _player = player;
                        player.Play();
                    },
                    onPcm: (buffer, length) =>
                    {
                        // Linux: pa_simple_write blocks → paces the decoder itself. Windows: feed the buffer.
                        var lo = _linuxOutput;
                        if (lo != null)
                        {
                            lo.Write(buffer, length);
                        }
                        else
                        {
                            _buffered?.AddSamples(buffer, 0, length);
                        }
                    },
                    waitIfFull: () =>
                    {
                        // Backpressure: keep the decoder ~3-4 s ahead so memory stays bounded (~768 KB).
                        BufferedWaveProvider? bp;
                        while ((bp = _buffered) != null
                               && !token.IsCancellationRequested
                               && bp.BufferedDuration > TimeSpan.FromSeconds(3))
                        {
                            Thread.Sleep(15);
                        }
                    },
                    token);
            }
            catch (OperationCanceledException)
            {
                // stopped or seeked — expected
            }
            catch (Exception ex)
            {
                // "No audio stream", device init failure, etc. Preview audio is best-effort: log and stay silent.
                Debug.WriteLine($"[PinAudio] streaming decode failed: {ex.Message}");
            }
        }, token);
    }

    /// <summary>Retimes audio by scaling the sample rate (pitch shifts with speed, like the Pin preview).</summary>
    internal static WaveFormat CreatePlaybackWaveFormat(WaveFormat sourceFormat, double playbackSpeed)
    {
        double safeSpeed = Math.Clamp(playbackSpeed, 0.25, 4.0);
        if (Math.Abs(safeSpeed - 1.0) < 0.01)
        {
            return sourceFormat;
        }

        int adjustedSampleRate = (int)Math.Round(sourceFormat.SampleRate * safeSpeed);
        adjustedSampleRate = Math.Clamp(adjustedSampleRate, 8_000, 192_000);
        return new WaveFormat(adjustedSampleRate, sourceFormat.BitsPerSample, sourceFormat.Channels);
    }

    /// <summary>
    /// Builds the playback pipeline that scales volume by multiplying samples in-stream. Returns the
    /// <see cref="VolumeSampleProvider"/> (so its <c>Volume</c> can be adjusted live) and the wave provider
    /// to feed the output device. Setting <see cref="IWavePlayer.Volume"/> instead would move this app's
    /// level in the Windows volume mixer (the whole app, not just the preview). Kept testable (no device).
    /// </summary>
    internal static (VolumeSampleProvider Volume, IWaveProvider Output) BuildVolumePipeline(IWaveProvider source, float volume)
    {
        var volumeProvider = new VolumeSampleProvider(source.ToSampleProvider()) { Volume = volume };
        return (volumeProvider, new SampleToWaveProvider(volumeProvider));
    }

    internal static (VolumeSampleProvider Volume, IWaveProvider Output) BuildVolumePipeline(WaveStream stream, float volume)
        => BuildVolumePipeline((IWaveProvider)stream, volume);

    private IWavePlayer CreateOutput(IWaveProvider source)
    {
        (VolumeSampleProvider volumeProvider, IWaveProvider output) = BuildVolumePipeline(source, _volume);
        _volumeProvider = volumeProvider;

        var wasapi = new WasapiOut();
        try
        {
            wasapi.Init(output);
            return wasapi;
        }
        catch
        {
            wasapi.Dispose();
        }

        var waveOut = new WaveOutEvent();
        waveOut.Init(output);
        return waveOut;
    }

    private IWavePlayer CreateOutput(WaveStream stream) => CreateOutput((IWaveProvider)stream);

    /// <summary>
    /// Cancels any in-flight PCM decode and hands the token source to the caller (dispose off-thread) —
    /// mirrors the Pin dispose flow which cancels on the UI thread and defers disposal.
    /// </summary>
    public CancellationTokenSource? CancelDecode()
    {
        var oldCts = Interlocked.Exchange(ref _decodeCts, null);
        if (oldCts != null)
        {
            try
            {
                oldCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        return oldCts;
    }

    /// <summary>Stops playback and releases the player/stream/decode token. Safe to call repeatedly.</summary>
    public void Stop()
    {
        try
        {
            _decodeCts?.Cancel();
            _decodeCts?.Dispose();
            _decodeCts = null;
            _player?.Stop();
            _player?.Dispose();
            _player = null;
            _volumeProvider = null;
            _stream?.Dispose();
            _stream = null;
            _linuxOutput?.Dispose(); // frees the pa_simple handle (Dispose waits out any in-flight blocking write)
            _linuxOutput = null;
            _buffered = null; // decode thread's onPcm/waitIfFull observe null + the cancelled token and exit
        }
        catch (Exception ex)
        {
            AppLog.Warning("AudioPreview.Stop", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
