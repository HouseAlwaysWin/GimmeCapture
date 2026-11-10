using System;
using NAudio.Wave;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// Plays the pieces of a wave stream one after the other without reopening anything, what lies between them left out,
/// each jump made inside one read so the output device never runs dry (<see cref="SoundPieces"/>). A piece lasts exactly
/// its length: a sound that ends before the piece does is filled with silence up to it. After the last piece, unless the
/// sound goes round, come silence until the caller stops it, so the device plays out what it holds and its clock runs on
/// under the last frame.
/// </summary>
internal sealed class PiecesWaveProvider : IWaveProvider
{
    private readonly WaveStream _source;
    private readonly long[] _startBytes;
    private readonly long[] _endBytes;
    private readonly bool _goesRound;
    private int _index;
    private long _position;
    private bool _over;

    public PiecesWaveProvider(WaveStream source, SoundPieces sound)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sound);
        _source = source;
        WaveFormat format = source.WaveFormat;
        int count = sound.Pieces.Count;
        _startBytes = new long[count];
        _endBytes = new long[count];
        for (int i = 0; i < count; i++)
        {
            _startBytes[i] = LoopingWaveProvider.BytesAt(sound.Pieces[i].StartSeconds, format);
            _endBytes[i] = Math.Max(_startBytes[i] + format.BlockAlign, LoopingWaveProvider.BytesAt(sound.Pieces[i].EndSeconds, format));
        }

        _goesRound = sound.GoesRound;
        _index = sound.First;
        _position = Math.Min(LoopingWaveProvider.BytesAt(sound.StartSeconds, format), _endBytes[_index] - format.BlockAlign);
        _source.Position = _position;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Always fills the whole request: the playback, not the sound, says when it is over.</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (!_over && _position >= _endBytes[_index])
            {
                MoveToNextPiece();
            }

            if (_over)
            {
                Array.Clear(buffer, offset + written, count - written);
                return count;
            }

            int wanted = (int)Math.Min(count - written, _endBytes[_index] - _position);
            int read = _source.Read(buffer, offset + written, wanted);
            if (read <= 0)
            {
                // The sound ended before the piece's end: silence for the rest of it.
                Array.Clear(buffer, offset + written, wanted);
                read = wanted;
            }

            written += read;
            _position += read;
        }

        return written;
    }

    private void MoveToNextPiece()
    {
        int next = _index + 1;
        if (next == _endBytes.Length)
        {
            if (!_goesRound)
            {
                _over = true;
                return;
            }

            next = 0;
        }

        _index = next;
        _position = _startBytes[next];
        _source.Position = _position;
    }
}
