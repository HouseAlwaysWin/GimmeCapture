using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.ViewModels.Floating;
using Xunit;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// Which timelines a video pin loops in one decode, the sound looping in step (<see cref="FloatingVideoViewModel"/>):
/// the whole clip, or one stretch of it, at 1×. Anything else is not one loop (FloatingVideoKeptPiecesTests).
/// </summary>
public class FloatingVideoSeamlessLoopTests
{
    private static VideoEditSegment Piece(double start, double end, bool kept = true, double speed = 1.0) =>
        new(start, end, speed) { Kept = kept };

    [Fact]
    public void AnUneditedPin_LoopsTheWholeClip_ToWhereverItsPictureEnds()
    {
        PlaybackLoop? loop = FloatingVideoViewModel.SeamlessLoopFor([], looping: true, playbackSpeed: 1.0);

        Assert.NotNull(loop);
        Assert.Equal(0, loop.Value.StartSeconds);
        Assert.True(double.IsNaN(loop.Value.EndSeconds));
    }

    [Fact]
    public void ATrim_LoopsItsStretch()
    {
        Assert.Equal(
            new PlaybackLoop(1.5, 4.0),
            FloatingVideoViewModel.SeamlessLoopFor([Piece(1.5, 4.0)], looping: true, playbackSpeed: 1.0));
    }

    [Fact]
    public void PiecesSplitButAllKept_LoopAsOneStretch()
    {
        Assert.Equal(
            new PlaybackLoop(0, 6),
            FloatingVideoViewModel.SeamlessLoopFor([Piece(0, 2), Piece(2, 6)], looping: true, playbackSpeed: 1.0));
    }

    [Fact]
    public void PiecesWithAGapBetween_AreNotOneLoop()
    {
        Assert.Null(FloatingVideoViewModel.SeamlessLoopFor([Piece(0, 2), Piece(4, 6)], looping: true, playbackSpeed: 1.0));
    }

    [Theory]
    [InlineData(false, 1.0, 1.0)] // not looping: it plays to the end and stops
    [InlineData(true, 1.5, 1.0)]  // the speed button: the sound is retimed
    [InlineData(true, 1.0, 2.0)]  // a piece's own speed
    public void OtherSpeedsAndNoLoop_AreNotOneLoop(bool looping, double playbackSpeed, double pieceSpeed)
    {
        Assert.Null(FloatingVideoViewModel.SeamlessLoopFor([Piece(0, 6, speed: pieceSpeed)], looping, playbackSpeed));
    }

    [Fact]
    public void AStretchOfAFrameOrTwo_IsNotALoop()
    {
        Assert.Null(FloatingVideoViewModel.SeamlessLoopFor([Piece(1.0, 1.03)], looping: true, playbackSpeed: 1.0));
    }

    [Fact]
    public void OpeningTheTimeline_DoesNotChangeTheLoop()
    {
        // The strip is seeded with one piece over the clip's length, which the player ends where the picture does.
        Assert.True(FloatingVideoViewModel.IsSameLoop(new PlaybackLoop(0, double.NaN), new PlaybackLoop(0, 6.038)));
        Assert.True(FloatingVideoViewModel.IsSameLoop(new PlaybackLoop(0, 6.038), new PlaybackLoop(0, 6.038)));
    }

    [Fact]
    public void ATrimOrADroppedPiece_ChangesTheLoop()
    {
        Assert.False(FloatingVideoViewModel.IsSameLoop(new PlaybackLoop(0, 6.038), new PlaybackLoop(1.0, 6.038)));
        Assert.False(FloatingVideoViewModel.IsSameLoop(new PlaybackLoop(0, 6.038), new PlaybackLoop(0, 4.0)));
        Assert.False(FloatingVideoViewModel.IsSameLoop(new PlaybackLoop(0, 6.038), null));
    }
}
