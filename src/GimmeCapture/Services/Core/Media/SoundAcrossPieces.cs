using System;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// One sound through all the pieces of a preview, as the decode reaches each of them. It starts at the first piece, and
/// from then on each piece reads its clock from that piece's own start, as the picture of a piece keeps to a clock that
/// starts with the piece: before the sound gets there the reading is below zero, and the picture waits for it.
/// </summary>
internal sealed class SoundAcrossPieces
{
    private readonly Func<long> _start;
    private readonly Func<long, SoundClockReading> _readClock;
    private bool _started;
    private long _ticket;
    private double _beforeThePiece;
    private double _thePiecesLength;

    /// <param name="start">Starts the sound, returning the ticket to read its clock with, 0 for no sound.</param>
    /// <param name="readClock">Reads the clock of the sound a ticket started.</param>
    public SoundAcrossPieces(Func<long> start, Func<long, SoundClockReading> readClock)
    {
        _start = start;
        _readClock = readClock;
    }

    /// <summary>The decode thread: the decode has reached <paramref name="piece"/>; returns its clock, null with no sound.</summary>
    public Func<SoundClockReading>? OnPieceStarts(PlaybackPiece piece)
    {
        if (!_started)
        {
            _started = true;
            _ticket = _start();
        }

        _beforeThePiece += _thePiecesLength;
        _thePiecesLength = Math.Max(0, piece.EndSeconds - piece.StartSeconds);
        if (_ticket == 0)
        {
            return null;
        }

        long ticket = _ticket;
        double before = _beforeThePiece;
        return () =>
        {
            SoundClockReading reading = _readClock(ticket);
            return reading.State == SoundClockState.Playing ? SoundClockReading.At(reading.Seconds - before) : reading;
        };
    }
}
