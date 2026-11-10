using GimmeCapture.Services.Core.Media;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// One sound through the pieces of a preview (<see cref="SoundAcrossPieces"/>): started once, and read for each piece from
/// that piece's own start, as the picture of a piece keeps to a clock that starts with the piece.
/// </summary>
public class SoundAcrossPiecesTests
{
    private static readonly PlaybackPiece First = new(9.5, 11, 1.0);   // begun partway: 1.5 s of it
    private static readonly PlaybackPiece Second = new(17, 20, 1.0);   // 3 s

    private int _starts;
    private long _ticketRead;
    private SoundClockReading _reading = SoundClockReading.At(0);

    private SoundAcrossPieces New(long ticket = 7) => new(
        () =>
        {
            _starts++;
            return ticket;
        },
        read =>
        {
            _ticketRead = read;
            return _reading;
        });

    [Fact]
    public void TheSound_StartsOnce_AtTheFirstPiece()
    {
        SoundAcrossPieces sound = New();

        sound.OnPieceStarts(First);
        sound.OnPieceStarts(Second);
        sound.OnPieceStarts(First);

        Assert.Equal(1, _starts);
    }

    [Fact]
    public void TheFirstPiece_ReadsTheSoundAsItIs()
    {
        Func<SoundClockReading> clock = New().OnPieceStarts(First)!;
        _reading = SoundClockReading.At(0.4);

        Assert.Equal(SoundClockReading.At(0.4), clock());
        Assert.Equal(7, _ticketRead);
    }

    [Fact]
    public void ALaterPiece_ReadsFromItsOwnStart()
    {
        SoundAcrossPieces sound = New();
        sound.OnPieceStarts(First);
        Func<SoundClockReading> clock = sound.OnPieceStarts(Second)!;

        _reading = SoundClockReading.At(2.0);

        Assert.Equal(SoundClockReading.At(0.5), clock()); // 1.5 s of the first piece, then half a second of this one
    }

    [Fact]
    public void BeforeTheSoundHasReachedAPiece_ItsReadingIsBelowZero()
    {
        SoundAcrossPieces sound = New();
        sound.OnPieceStarts(First);
        Func<SoundClockReading> clock = sound.OnPieceStarts(Second)!;

        _reading = SoundClockReading.At(1.4);

        Assert.True(clock().Seconds < 0);
    }

    [Fact]
    public void Rounds_AddUp()
    {
        SoundAcrossPieces sound = New();
        sound.OnPieceStarts(First);
        sound.OnPieceStarts(Second);
        Func<SoundClockReading> clock = sound.OnPieceStarts(First)!;

        _reading = SoundClockReading.At(5.0);

        Assert.Equal(SoundClockReading.At(0.5), clock()); // 1.5 + 3 s before this piece
    }

    [Fact]
    public void ASoundStillOpening_OrGone_IsPassedOnAsItStands()
    {
        SoundAcrossPieces sound = New();
        sound.OnPieceStarts(First);
        Func<SoundClockReading> clock = sound.OnPieceStarts(Second)!;

        _reading = SoundClockReading.Pending;
        Assert.Equal(SoundClockReading.Pending, clock());

        _reading = SoundClockReading.Unavailable;
        Assert.Equal(SoundClockReading.Unavailable, clock());
    }

    [Fact]
    public void WithNoSound_NoPieceHasAClock()
    {
        SoundAcrossPieces sound = New(ticket: 0);

        Assert.Null(sound.OnPieceStarts(First));
        Assert.Null(sound.OnPieceStarts(Second));
        Assert.Equal(1, _starts);
    }
}
