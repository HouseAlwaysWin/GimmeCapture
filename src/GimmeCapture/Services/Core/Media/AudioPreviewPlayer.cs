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
/// WasapiOut and falls back to WaveOutEvent.
/// <para><see cref="Start"/> and <see cref="Stop"/> only record what the caller wants, so they are safe from any thread
/// and never block it: opening a reader and an output device takes 30–150 ms, which the pin spent on the UI thread at
/// every seek, speed change and mute. One worker does that work, a request at a time and the newest first, so a seek
/// made while an earlier start is still opening waits for it instead of racing it, and the earlier one never plays.
/// The worker's thread is in the MTA. Media Foundation is started there by the reader, which opens the file only to
/// read its format and opens it again for reading on the output's own playback thread (another MTA thread), so its
/// COM objects never cross apartments.</para>
/// </summary>
internal sealed class AudioPreviewPlayer : IDisposable
{
    // How long Dispose waits for the device and the file to be let go: a pin deletes its temporary recording next.
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

    private readonly LatestRequestWorker<Request> _requests;

    // What is playing. The worker puts it in place and takes it down; the decode thread puts its own output in place;
    // Volume adjusts it. All three under _outputGate, so none of them misses a change the others make.
    private readonly object _outputGate = new();
    private IWavePlayer? _player;
    private WaveStream? _stream;
    private VolumeSampleProvider? _volumeProvider;
    private LinuxPulseAudioOutput? _linuxOutput; // Linux audio output (NAudio IWavePlayer is Windows-only)
    private CancellationTokenSource? _decodeCts;
    private Task? _decodeTask;
    private volatile bool _disposed;
    private float _volume = 1f;

    // A start of the file at a position and speed, or a stop when VideoPath is null.
    private readonly record struct Request(string? VideoPath, double StartSeconds, double EffectiveSpeed);

    public AudioPreviewPlayer()
    {
        _requests = new LatestRequestWorker<Request>(Handle, "AudioPreview.Request");
    }

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
            lock (_outputGate)
            {
                _volume = Math.Clamp(value, 0f, 1f);
                if (_volumeProvider != null)
                {
                    _volumeProvider.Volume = _volume;
                }

                if (_linuxOutput != null)
                {
                    _linuxOutput.Volume = _volume;
                }
            }
        }
    }

    /// <summary>
    /// (Re)start audio at <paramref name="startSeconds"/> with the given effective speed. Any previous
    /// playback is stopped first. Failures fall back to the decoded path, then log and stay silent
    /// (preview audio is best-effort). Returns at once; the audio follows shortly.
    /// </summary>
    public void Start(string videoPath, double startSeconds, double effectiveSpeed)
    {
        if (_disposed || string.IsNullOrEmpty(videoPath))
        {
            Stop();
            return;
        }

        _requests.Submit(new Request(videoPath, Math.Max(0, startSeconds), effectiveSpeed));
    }

    /// <summary>Stops playback and releases the player/stream/decode token. Safe to call repeatedly. Returns at once.</summary>
    public void Stop() => _requests.Submit(default);

    // On the worker: whatever played stops, then the request starts, unless a newer one is already waiting.
    private void Handle(Request request, Func<bool> superseded)
    {
        StopCore();
        if (request.VideoPath is not { } videoPath || _disposed || superseded())
        {
            return;
        }

        StartCore(videoPath, request.StartSeconds, request.EffectiveSpeed, superseded);
    }

    private void StartCore(string videoPath, double start, double effectiveSpeed, Func<bool> superseded)
    {
        try
        {
            if (ShouldUseDecodedPlayback(videoPath, effectiveSpeed))
            {
                StartDecodedPlayback(videoPath, start, effectiveSpeed);
                return;
            }

            StartMediaFoundationPlayback(videoPath, start, superseded);
        }
        catch (Exception ex)
        {
            if (_disposed)
            {
                return;
            }

            AppLog.Warning("PinAudio.PrimaryOutput", ex);
            StopCore();

            try
            {
                StartDecodedPlayback(videoPath, start, effectiveSpeed);
            }
            catch (Exception fallbackEx)
            {
                AppLog.Warning("PinAudio.FfmpegFallback", fallbackEx);
                StopCore();
            }
        }
    }

    private void StartMediaFoundationPlayback(string videoPath, double start, Func<bool> superseded)
    {
        MediaFoundationReader? reader = null;
        IWavePlayer? player = null;
        try
        {
            reader = new MediaFoundationReader(videoPath);
            reader.CurrentTime = TimeSpan.FromSeconds(start);
            player = CreateOutput(reader, out VolumeSampleProvider volumeProvider);

            lock (_outputGate)
            {
                // A newer request came in while the file opened: it runs next, so this one is never heard.
                if (_disposed || superseded())
                {
                    return;
                }

                volumeProvider.Volume = _volume;
                _stream = reader;
                _player = player;
                _volumeProvider = volumeProvider;
                reader = null;
                IWavePlayer playing = player;
                player = null;
                playing.Play(); // if this throws, StopCore still finds and disposes it
            }
        }
        finally
        {
            player?.Dispose();
            reader?.Dispose();
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

        var cts = new CancellationTokenSource();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _decodeCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        var token = cts.Token;

        // Stream the audio: start the device on an initially-empty BufferedWaveProvider, then feed it from a
        // background decode. First audio lands in ~100-200 ms; memory stays bounded (decoder throttled ~4 s
        // ahead) regardless of file size — versus the old full-file PCM decode that blocked the play loop and
        // held hundreds of MB in a MemoryStream.
        _decodeTask = Task.Run(() =>
        {
            // This decode's own output, so a decode that a newer request cancelled never feeds the next one's device.
            BufferedWaveProvider? buffered = null;
            LinuxPulseAudioOutput? linuxOutput = null;
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
                            // gave on Windows — waitIfFull below stays a no-op (there's no buffer on Linux).
                            var output = new LinuxPulseAudioOutput { Volume = _volume };
                            if (output.Start(retimed.SampleRate, retimed.Channels) && TryAdoptLinuxOutput(output, token))
                            {
                                linuxOutput = output;
                            }
                            else
                            {
                                output.Dispose();
                            }

                            return;
                        }

                        var bufferedProvider = new BufferedWaveProvider(retimed)
                        {
                            BufferDuration = TimeSpan.FromSeconds(4),
                            DiscardOnBufferOverflow = false,
                            ReadFully = true, // underrun => silence, never signals end-of-stream (device won't stop)
                        };

                        IWavePlayer player = CreateOutput(bufferedProvider, out VolumeSampleProvider volumeProvider);
                        if (TryAdoptOutput(player, volumeProvider, token))
                        {
                            buffered = bufferedProvider;
                        }
                        else
                        {
                            player.Dispose();
                        }
                    },
                    onPcm: (buffer, length) =>
                    {
                        // Linux: pa_simple_write blocks → paces the decoder itself. Windows: feed the buffer.
                        if (linuxOutput != null)
                        {
                            linuxOutput.Write(buffer, length);
                        }
                        else
                        {
                            buffered?.AddSamples(buffer, 0, length);
                        }
                    },
                    waitIfFull: () =>
                    {
                        // Backpressure: keep the decoder ~3-4 s ahead so memory stays bounded (~768 KB).
                        while (buffered != null
                               && !token.IsCancellationRequested
                               && buffered.BufferedDuration > TimeSpan.FromSeconds(3))
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
                AppLog.Debug("PinAudio.StreamingDecode", ex);
            }
        }, token);
    }

    // The decode thread's device goes in place only while its decode is still wanted: once a stop or a newer request
    // has cancelled it, the caller disposes the device, so nothing it opened outlives that stop.
    private bool TryAdoptOutput(IWavePlayer player, VolumeSampleProvider volumeProvider, CancellationToken token)
    {
        lock (_outputGate)
        {
            if (_disposed || token.IsCancellationRequested)
            {
                return false;
            }

            volumeProvider.Volume = _volume;
            _player = player;
            _volumeProvider = volumeProvider;
            player.Play(); // if this throws, StopCore still finds and disposes it
            return true;
        }
    }

    private bool TryAdoptLinuxOutput(LinuxPulseAudioOutput output, CancellationToken token)
    {
        lock (_outputGate)
        {
            if (_disposed || token.IsCancellationRequested)
            {
                return false;
            }

            output.Volume = _volume;
            _linuxOutput = output;
            return true;
        }
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

    // Opens the output device for source. The volume stage it returns is put in place with the device, under _outputGate.
    private IWavePlayer CreateOutput(IWaveProvider source, out VolumeSampleProvider volumeProvider)
    {
        (volumeProvider, IWaveProvider output) = BuildVolumePipeline(source, _volume);

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

    // On the worker: stops and releases whatever plays. A decode still running sees its token cancelled and stops
    // feeding; it can no longer put a device in place.
    private void StopCore()
    {
        CancellationTokenSource? decodeCts = Interlocked.Exchange(ref _decodeCts, null);
        if (decodeCts != null)
        {
            try
            {
                decodeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            decodeCts.Dispose();
        }

        IWavePlayer? player;
        WaveStream? stream;
        LinuxPulseAudioOutput? linuxOutput;
        lock (_outputGate)
        {
            player = _player;
            stream = _stream;
            linuxOutput = _linuxOutput;
            _player = null;
            _stream = null;
            _linuxOutput = null;
            _volumeProvider = null;
        }

        try
        {
            player?.Stop();
            player?.Dispose();
            stream?.Dispose();
            linuxOutput?.Dispose(); // frees the pa_simple handle (Dispose waits out any in-flight blocking write)
        }
        catch (Exception ex)
        {
            AppLog.Warning("AudioPreview.Stop", ex);
        }
    }

    /// <summary>Stops playback and waits, two seconds at most, until the device and the file are let go.</summary>
    public void Dispose()
    {
        _disposed = true;
        Stop();

        var waited = Stopwatch.StartNew();
        bool released = _requests.WaitUntilIdle(DisposeTimeout);

        // The stop cancelled the last decode; it closes the file as soon as it notices.
        Task? decode = _decodeTask;
        TimeSpan left = DisposeTimeout - waited.Elapsed;
        if (released && decode != null && left > TimeSpan.Zero)
        {
            try
            {
                released = decode.Wait(left);
            }
            catch (AggregateException)
            {
                // cancelled before it started: nothing was opened
            }
        }

        if (!released)
        {
            AppLog.Debug("AudioPreview.Dispose", $"still releasing the audio device or file after {DisposeTimeout.TotalSeconds:0} s");
        }
    }
}
