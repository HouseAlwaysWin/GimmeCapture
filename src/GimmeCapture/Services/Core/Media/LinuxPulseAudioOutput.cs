using System;
using System.Runtime.InteropServices;
using System.Threading;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// PulseAudio playback via libpulse-simple (pa_simple) — the Linux counterpart of the NAudio
/// <c>IWavePlayer</c> output that <see cref="AudioPreviewPlayer"/> uses on Windows. Plays interleaved
/// 16-bit PCM; <c>pa_simple_write</c> blocks until the data is buffered, which paces the decode thread
/// naturally (no separate BufferedWaveProvider needed). Volume scales the samples on write.
/// </summary>
internal sealed class LinuxPulseAudioOutput : IDisposable
{
    private const int PA_STREAM_PLAYBACK = 1;
    private const int PA_SAMPLE_S16LE = 3;

    private readonly object _gate = new(); // serialize the blocking write against free (no use-after-free)
    private IntPtr _pa;
    private volatile float _volume = 1f;

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public bool Start(int sampleRate, int channels)
    {
        try
        {
            var ss = new PaSampleSpec
            {
                format = PA_SAMPLE_S16LE,
                rate = (uint)Math.Clamp(sampleRate, 8000, 192000),
                channels = (byte)Math.Clamp(channels, 1, 8),
            };

            _pa = pa_simple_new(null, "GimmeCapture", PA_STREAM_PLAYBACK, null, "preview", ref ss, IntPtr.Zero, IntPtr.Zero, out int err);
            if (_pa == IntPtr.Zero)
            {
                AppLog.Information($"LinuxPulseAudioOutput.Start failed err={err}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warning("LinuxPulseAudioOutput.Start", ex);
            return false;
        }
    }

    public void Write(byte[] pcm, int length)
    {
        if (length <= 0)
        {
            return;
        }

        byte[] data = pcm;
        float vol = _volume;
        if (vol < 0.999f)
        {
            // Scale s16 samples in a copy so the caller's buffer is untouched.
            data = new byte[length];
            for (int i = 0; i + 1 < length; i += 2)
            {
                short s = (short)(pcm[i] | (pcm[i + 1] << 8));
                int scaled = Math.Clamp((int)(s * vol), short.MinValue, short.MaxValue);
                data[i] = (byte)(scaled & 0xFF);
                data[i + 1] = (byte)((scaled >> 8) & 0xFF);
            }
        }

        try
        {
            lock (_gate)
            {
                if (_pa != IntPtr.Zero)
                {
                    pa_simple_write(_pa, data, (UIntPtr)length, out _); // blocking → natural backpressure
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("LinuxPulseAudioOutput.Write", ex);
        }
    }

    public void Dispose()
    {
        // The lock waits for any in-flight (blocking) write to finish before freeing the handle.
        lock (_gate)
        {
            var pa = Interlocked.Exchange(ref _pa, IntPtr.Zero);
            if (pa != IntPtr.Zero)
            {
                try { pa_simple_free(pa); } catch { /* best effort */ }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaSampleSpec
    {
        public int format;
        public uint rate;
        public byte channels;
    }

    [DllImport("libpulse-simple.so.0")]
    private static extern IntPtr pa_simple_new(string? server, string name, int dir, string? dev, string streamName, ref PaSampleSpec ss, IntPtr map, IntPtr attr, out int error);

    [DllImport("libpulse-simple.so.0")]
    private static extern int pa_simple_write(IntPtr s, byte[] data, UIntPtr bytes, out int error);

    [DllImport("libpulse-simple.so.0")]
    private static extern void pa_simple_free(IntPtr s);
}
