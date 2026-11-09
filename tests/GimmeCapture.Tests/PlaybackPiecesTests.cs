using GimmeCapture.Services.Core.Media;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>Which piece of a cut video the editor's preview is on, and which comes after it.</summary>
public sealed class PlaybackPiecesTests
{
    private static readonly PlaybackPiece First = new(1, 2, 1.0);
    private static readonly PlaybackPiece Second = new(5, 6, 2.0);

    [Fact]
    public void ThePlayback_BeginsWhereItIsAsked_AndTheNextPieceFromItsStart()
    {
        var pieces = new PlaybackPieces([First, Second], 0, 1.5, () => false);

        Assert.Equal(First with { StartSeconds = 1.5 }, pieces.Current);
        Assert.True(pieces.MoveNext(pieceShowedAFrame: true));
        Assert.Equal(Second, pieces.Current);
    }

    [Fact]
    public void AfterTheLastPiece_NothingFollows_UnlessARoundIsWanted()
    {
        var once = new PlaybackPieces([First, Second], 1, Second.StartSeconds, () => false);
        Assert.False(once.MoveNext(pieceShowedAFrame: true));

        var round = new PlaybackPieces([First, Second], 1, 5.5, () => true);
        Assert.True(round.MoveNext(pieceShowedAFrame: true));
        Assert.Equal(First, round.Current);
    }

    [Fact]
    public void TheWishForARound_IsAskedWhenTheLastPieceIsOver_NotBefore()
    {
        bool goesRound = false;
        var pieces = new PlaybackPieces([First, Second], 0, First.StartSeconds, () => goesRound);

        Assert.True(pieces.MoveNext(pieceShowedAFrame: true));
        goesRound = true; // Loop switched on during the last piece
        Assert.True(pieces.MoveNext(pieceShowedAFrame: true));
        Assert.Equal(First, pieces.Current);
    }

    [Theory]
    [InlineData(1.9994, false)]
    [InlineData(1.9996, true)]
    [InlineData(2.0, true)]
    [InlineData(2.5, true)]
    public void AFrameEndsThePiece_AtItsEnd_WithinTheDecodersRounding(double frameSeconds, bool ends)
    {
        var pieces = new PlaybackPieces([First], 0, First.StartSeconds, () => false);

        Assert.Equal(ends, pieces.EndsPiece(frameSeconds));
    }

    [Fact]
    public void AWholeRoundWithoutAFrame_EndsThePlayback()
    {
        var pieces = new PlaybackPieces([First, Second], 0, First.StartSeconds, () => true);

        // The first round showed something, so the second is tried.
        Assert.True(pieces.MoveNext(pieceShowedAFrame: true));
        Assert.True(pieces.MoveNext(pieceShowedAFrame: false));

        // The second shows nothing at all.
        Assert.True(pieces.MoveNext(pieceShowedAFrame: false));
        Assert.False(pieces.MoveNext(pieceShowedAFrame: false));
    }

    [Fact]
    public void AFrameInAnyPieceOfTheRound_IsEnoughToGoRoundAgain()
    {
        var pieces = new PlaybackPieces([First, Second], 0, First.StartSeconds, () => true);
        Assert.True(pieces.MoveNext(pieceShowedAFrame: false));
        Assert.True(pieces.MoveNext(pieceShowedAFrame: false)); // the first round: only partway, it proves nothing

        Assert.True(pieces.MoveNext(pieceShowedAFrame: true));
        Assert.True(pieces.MoveNext(pieceShowedAFrame: false));
        Assert.Equal(First, pieces.Current);
    }

    [Fact]
    public void AFirstRoundThatBeganTooLateToShowAFrame_StillGoesRound()
    {
        // Play pressed within the last frame's time of the last piece, with Loop on.
        var pieces = new PlaybackPieces([First, Second], 1, 5.9999, () => true);

        Assert.True(pieces.MoveNext(pieceShowedAFrame: false));
        Assert.Equal(First, pieces.Current);
    }
}
