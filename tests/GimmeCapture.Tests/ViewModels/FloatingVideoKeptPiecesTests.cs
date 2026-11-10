using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.ViewModels.Floating;
using Xunit;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// What a video pin with pieces cut out, or at other speeds, plays in its one decode
/// (<see cref="FloatingVideoViewModel.PiecesFor"/>). Played a pass per piece, each piece opened the file again: on a
/// 1450x900 recording the last frame before a cut stood 182-221 ms, and the sound came another amount off the picture
/// each piece.
/// </summary>
public class FloatingVideoKeptPiecesTests
{
    private static readonly VideoEditSegment[] TwoCuts = [Piece(1, 3), Piece(8, 11), Piece(17, 20)];

    private static VideoEditSegment Piece(double start, double end, double speed = 1.0) => new(start, end, speed);

    private static PlaybackPieces? From(double seconds, bool looping = false, double playbackSpeed = 1.0) =>
        FloatingVideoViewModel.PiecesFor(TwoCuts, seconds, playbackSpeed, () => looping);

    [Fact]
    public void APlaybackFromInsideAPiece_BeginsThere_AndGoesOnToTheNext()
    {
        PlaybackPieces pieces = From(9.5)!;

        Assert.Equal(new PlaybackPiece(9.5, 11, 1.0), pieces.Current);
        Assert.True(pieces.MoveNext(pieceShowedAFrame: true));
        Assert.Equal(new PlaybackPiece(17, 20, 1.0), pieces.Current);
    }

    [Theory]
    [InlineData(0.0, 1.0)]   // before the first piece
    [InlineData(5.0, 8.0)]   // in a cut
    [InlineData(11.0, 17.0)] // at the end of a piece, the cut after it
    public void ATimeThatIsCutOut_BeginsAtTheNextPiece(double seconds, double expected)
    {
        Assert.Equal(expected, From(seconds)!.Current.StartSeconds);
    }

    [Fact]
    public void PastTheLastPiece_TheClipIsOver()
    {
        Assert.Null(From(25));
    }

    [Fact]
    public void PastTheLastPiece_ALoopBeginsAgainAtTheFirst()
    {
        Assert.Equal(new PlaybackPiece(1, 3, 1.0), From(25, looping: true)!.Current);
    }

    [Fact]
    public void TheLastPiece_GoesRoundWhileItIsLooping()
    {
        bool looping = true;
        PlaybackPieces pieces = FloatingVideoViewModel.PiecesFor(TwoCuts, 18, 1.0, () => looping)!;

        Assert.True(pieces.MoveNext(pieceShowedAFrame: true));
        Assert.Equal(1, pieces.Current.StartSeconds);

        looping = false;
        pieces = FloatingVideoViewModel.PiecesFor(TwoCuts, 18, 1.0, () => looping)!;
        Assert.False(pieces.MoveNext(pieceShowedAFrame: true));
    }

    [Theory]
    [InlineData(1.0, 2.0, 2.0)]
    [InlineData(2.0, 2.0, 4.0)]
    [InlineData(1.5, 0.5, 0.75)]
    public void APiecesSpeed_IsItsOwnTimesThePlaybackSpeed(double playbackSpeed, double pieceSpeed, double expected)
    {
        PlaybackPieces pieces = FloatingVideoViewModel.PiecesFor([Piece(1, 3, pieceSpeed)], 1, playbackSpeed, () => false)!;

        Assert.Equal(expected, pieces.Current.Speed);
    }

    [Theory]
    [InlineData(true, false, 19.97, 34.6, true)]   // Play on the last frame after a playback to the end
    [InlineData(true, true, 5.0, 34.6, false)]     // a seek after that goes where it was asked
    [InlineData(false, false, 19.97, 34.6, false)] // Play after a pause goes on from the playhead
    [InlineData(false, false, 34.6, 34.6, true)]   // from the very end
    [InlineData(false, true, 40.0, 34.6, true)]    // a seek past it
    [InlineData(false, false, 3.0, 0.0, false)]    // the length not known yet
    public void Play_StartsOver_OnlyAtTheEnd(bool playedToTheEnd, bool seekAsked, double from, double end, bool startsOver)
    {
        Assert.Equal(startsOver, FloatingVideoViewModel.StartsOver(playedToTheEnd, seekAsked, from, end));
    }

    [Fact]
    public void APieceWithoutASpeed_PlaysAtThePlaybackSpeed()
    {
        Assert.Equal(1.5, FloatingVideoViewModel.PiecesFor([Piece(1, 3, 0)], 1, 1.5, () => false)!.Current.Speed);
    }
}
