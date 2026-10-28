using GimmeCapture.Services.Core.Media;
using Xunit;

namespace GimmeCapture.Tests;

// A playing video pin's decoder records each frame's time here instead of setting CurrentTime (and rebuilding the
// redaction preview) off the UI thread; the throttled UI refresh takes it from here.
public class PlayheadHandoffTests
{
    [Fact]
    public void Taking_GivesTheLatestPosition_Once()
    {
        var playhead = new PlayheadHandoff();
        playhead.Record(1.0, generation: 3);
        playhead.Record(1.5, generation: 3);

        Assert.True(playhead.TryTake(3, out double seconds));
        Assert.Equal(1.5, seconds);
        Assert.False(playhead.TryTake(3, out _));
    }

    [Fact]
    public void APositionFromAPlaybackASeekReplaced_IsNeverGiven()
    {
        var playhead = new PlayheadHandoff();
        playhead.Record(9.0, generation: 3);

        Assert.False(playhead.TryTake(4, out _));
        Assert.False(playhead.TryPeek(4, out _));
    }

    [Fact]
    public void Peeking_LeavesThePositionToBeTaken_AndStillSeesItAfterwards()
    {
        var playhead = new PlayheadHandoff();
        playhead.Record(2.0, generation: 1);

        Assert.True(playhead.TryPeek(1, out double peeked));
        Assert.True(playhead.TryTake(1, out double taken));
        Assert.True(playhead.TryPeek(1, out double peekedAfter));
        Assert.Equal(2.0, peeked);
        Assert.Equal(2.0, taken);
        Assert.Equal(2.0, peekedAfter);
    }

    [Fact]
    public void ANewPosition_CanBeTakenAgain()
    {
        var playhead = new PlayheadHandoff();
        playhead.Record(1.0, generation: 1);
        Assert.True(playhead.TryTake(1, out _));

        playhead.Record(1.1, generation: 1);

        Assert.True(playhead.TryTake(1, out double seconds));
        Assert.Equal(1.1, seconds);
    }

    [Fact]
    public void BeforeAnyFrame_ThereIsNothingToGive()
    {
        var playhead = new PlayheadHandoff();

        Assert.False(playhead.TryTake(0, out _));
        Assert.False(playhead.TryPeek(0, out _));
    }
}
