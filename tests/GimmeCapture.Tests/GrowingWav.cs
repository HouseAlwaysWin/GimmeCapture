using System;
using System.Threading;
using NAudio.Wave;

namespace GimmeCapture.Tests;

/// <summary>
/// A WAV written the way a recording's capture writes one: opened, filled a burst at a time while something may read
/// it, then closed. Its sound is a few sines and seeded noise, different in every channel, so it is no easy encode.
/// </summary>
internal sealed class GrowingWav : IDisposable
{
    private readonly WaveFileWriter _writer;
    private readonly byte[] _sound;
    private int _written;

    public GrowingWav(string path, WaveFormat format, double seconds, int seed)
    {
        Path = path;
        _sound = Sound(format, seconds, seed);
        _writer = new WaveFileWriter(path, format);
    }

    public string Path { get; }

    public bool Done => _written == _sound.Length;

    /// <summary>Writes the next <paramref name="seconds"/> of the sound, or what is left of it.</summary>
    public void WriteBurst(double seconds = 0.05)
    {
        int bytes = (int)Math.Min(
            _sound.Length - _written,
            Math.Round(seconds * _writer.WaveFormat.SampleRate) * _writer.WaveFormat.BlockAlign);
        _writer.Write(_sound, _written, bytes);
        _written += bytes;
    }

    /// <summary>The rest of the sound, a burst at a time, each of the captures in turn, with a pause after each round.</summary>
    public static void WriteTogether(TimeSpan pause, params GrowingWav[] captures) => WriteTogether(0.05, pause, captures);

    /// <inheritdoc cref="WriteTogether(TimeSpan, GrowingWav[])"/>
    public static void WriteTogether(double burstSeconds, TimeSpan pause, params GrowingWav[] captures)
    {
        while (Array.Exists(captures, c => !c.Done))
        {
            foreach (var capture in captures)
            {
                capture.WriteBurst(burstSeconds);
            }

            Thread.Sleep(pause);
        }
    }

    /// <summary>Closes the file, as the capture's stop does.</summary>
    public void Dispose() => _writer.Dispose();

    public static byte[] Sound(WaveFormat format, double seconds, int seed)
    {
        var random = new Random(seed);
        int frames = (int)Math.Round(seconds * format.SampleRate);
        var pcm = new byte[frames * format.BlockAlign];
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / format.SampleRate;
            for (int channel = 0; channel < format.Channels; channel++)
            {
                double f = 220 * (channel + 1) + (40 * Math.Sin(2 * Math.PI * 0.5 * t));
                double sample = (0.25 * Math.Sin(2 * Math.PI * f * t)) + (0.12 * Math.Sin(2 * Math.PI * 3.1 * f * t))
                    + (0.05 * ((random.NextDouble() * 2) - 1));
                BitConverter.TryWriteBytes(
                    pcm.AsSpan((i * format.BlockAlign) + (channel * 2)), (short)(sample * short.MaxValue));
            }
        }

        return pcm;
    }
}
