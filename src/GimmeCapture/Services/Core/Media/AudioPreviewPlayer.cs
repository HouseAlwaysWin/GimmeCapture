using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
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
    private volatile bool _disposed;
    private float _volume = 1f;

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
        var decoded = LibavPinAudioPcmDecoder.Decode(videoPath, startSeconds, token);
        if (_disposed || token.IsCancellationRequested)
        {
            return;
        }

        if (decoded.PcmBytes.Length == 0)
        {
            throw new InvalidOperationException("Decoded PCM is empty.");
        }

        var playbackWaveFormat = CreatePlaybackWaveFormat(decoded.WaveFormat, playbackSpeed);
        var stream = new RawSourceWaveStream(new MemoryStream(decoded.PcmBytes, writable: false), playbackWaveFormat);
        if (_disposed || token.IsCancellationRequested)
        {
            stream.Dispose();
            return;
        }

        IWavePlayer? player = null;
        try
        {
            player = CreateOutput(stream);
            if (_disposed || token.IsCancellationRequested)
            {
                player.Dispose();
                stream.Dispose();
                return;
            }

            _stream = stream;
            _player = player;
            player.Play();
        }
        catch
        {
            player?.Dispose();
            stream.Dispose();
            throw;
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

    private IWavePlayer CreateOutput(WaveStream stream)
    {
        // Scale volume by multiplying samples in the pipeline. Setting IWavePlayer.Volume instead would
        // move this app's level in the Windows volume mixer (the whole app, not just the preview).
        _volumeProvider = new VolumeSampleProvider(stream.ToSampleProvider()) { Volume = _volume };
        IWaveProvider output = new SampleToWaveProvider(_volumeProvider);

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
