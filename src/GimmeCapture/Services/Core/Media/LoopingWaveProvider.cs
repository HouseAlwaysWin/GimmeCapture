using System;
using NAudio.Wave;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Plays a <see cref="PlaybackLoop"/> of a wave stream over and over without reopening anything: from where the
/// playback begins to the loop's end, then from the loop's start to its end again, the jump made inside one read so the
/// output device never runs dry. A video pin's sound used to be stopped and opened again at every pass, file and device
/// both, so each pass began a different 30–150 ms after its picture. Every pass lasts exactly the loop's length: a sound
/// that ends before the loop's end is filled with silence up to it, and one that runs on past it is cut there, the way
/// the picture's passes are laid out (<see cref="LoopPassTimeline"/>).
/// </summary>
internal sealed class LoopingWaveProvider : IWaveProvider
{
    private readonly WaveStream _source;
    private readonly long _loopStartBytes;
    private readonly long _loopEndBytes;
    private long _position; // where the playback stands in the stream, in bytes

    public LoopingWaveProvider(WaveStream source, double startSeconds, PlaybackLoop loop)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        WaveFormat format = source.WaveFormat;
        _loopStartBytes = BytesAt(loop.StartSeconds, format);
        _loopEndBytes = Math.Max(_loopStartBytes + format.BlockAlign, BytesAt(loop.EndSeconds, format));
        _position = Math.Min(BytesAt(loop.ClampStart(startSeconds), format), _loopEndBytes - format.BlockAlign);
        _source.Position = _position;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Always fills the whole request: the loop never ends, so neither does the sound.</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_position >= _loopEndBytes)
            {
                _source.Position = _loopStartBytes;
                _position = _loopStartBytes;
            }

            int wanted = (int)Math.Min(count - written, _loopEndBytes - _position);
            int read = _source.Read(buffer, offset + written, wanted);
            if (read <= 0)
            {
                // The sound ended before the loop's end: silence for the rest of the pass.
                Array.Clear(buffer, offset + written, wanted);
                read = wanted;
            }

            written += read;
            _position += read;
        }

        return written;
    }

    /// <summary>The byte at <paramref name="seconds"/>, on a whole sample frame.</summary>
    internal static long BytesAt(double seconds, WaveFormat format)
    {
        long bytes = (long)Math.Round(Math.Max(0, seconds) * format.AverageBytesPerSecond);
        return bytes - (bytes % format.BlockAlign);
    }
}
