using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// How a video pin lays out its loop: where it begins, how its passes run on one after another
/// (<see cref="LoopPassTimeline"/>), and where it ends, which the picture and the sound must agree on to stay together.
/// </summary>
public class PlaybackLoopTests
{
    [Theory]
    [InlineData(2.5, 2.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(3.0, 1.0)] // the loop's end belongs to the next pass
    [InlineData(0.5, 1.0)]
    [InlineData(7.0, 1.0)]
    public void APlayback_BeginsWhereItAsked_OnlyInsideTheLoop(double asked, double begins)
    {
        Assert.Equal(begins, new PlaybackLoop(1.0, 3.0).ClampStart(asked));
    }

    [Fact]
    public void ALoopTooShortOrWithoutAnEnd_DoesNotLoop()
    {
        Assert.True(new PlaybackLoop(1.0, 1.1).CanLoop);
        Assert.False(new PlaybackLoop(1.0, 1.04).CanLoop);
        Assert.False(new PlaybackLoop(0, double.NaN).CanLoop);
    }

    [Fact]
    public void TheTimeRunsOnAcrossPasses_TheFirstFromWhereThePlaybackBegan()
    {
        var timeline = new LoopPassTimeline(new PlaybackLoop(1.0, 3.0), startSeconds: 2.5);

        Assert.Equal(2.5, timeline.PassStartSeconds);
        Assert.True(timeline.IsBeforePassStart(2.4));   // decoded on the way from the keyframe, not shown
        Assert.False(timeline.IsBeforePassStart(2.5));
        Assert.Equal(0.0, timeline.PlaybackSeconds(2.5), 9);
        Assert.Equal(0.4, timeline.PlaybackSeconds(2.9), 9);
        Assert.False(timeline.EndsPass(2.99));
        Assert.True(timeline.EndsPass(3.0));

        timeline.NextPass();
        Assert.Equal(1.0, timeline.PassStartSeconds);
        Assert.Equal(0.5, timeline.PlaybackSeconds(1.0), 9); // the first pass lasted half a second
        Assert.Equal(2.0, timeline.PlaybackSeconds(2.5), 9);

        timeline.NextPass();
        Assert.Equal(2.5, timeline.PlaybackSeconds(1.0), 9); // every later pass lasts the loop's two seconds
        Assert.Equal(2, timeline.PassesEnded);
    }

    [Fact]
    public void APlaybackAskedToBeginOutsideTheLoop_StartsItsFirstPassAtTheLoopsStart()
    {
        var timeline = new LoopPassTimeline(new PlaybackLoop(1.0, 3.0), startSeconds: 3.5);

        Assert.Equal(1.0, timeline.PassStartSeconds);
        Assert.Equal(0.0, timeline.PlaybackSeconds(1.0), 9);
    }

    [Theory]
    [InlineData(double.NaN, 6.0, 6.0)]  // the whole clip: wherever the picture ends
    [InlineData(6.038, 6.0, 6.0)]       // a sound that runs on past the picture is cut where the picture ends
    [InlineData(4.0, 6.0, 4.0)]         // a stretch inside the clip ends where it ends
    [InlineData(4.0, 0, 4.0)]           // the picture's end unknown: the stretch's own end
    public void ALoop_EndsWhereThePictureDoesAtTheLatest(double askedEnd, double pictureEnd, double plays)
    {
        PlaybackLoop resolved = LibavVideoFramePlayer.ResolveLoop(new PlaybackLoop(1.0, askedEnd), pictureEnd);

        Assert.Equal(1.0, resolved.StartSeconds);
        Assert.Equal(plays, resolved.EndSeconds);
    }

    [Fact]
    public void TheWholeClip_CannotLoop_WhenTheFileDoesNotSayWhereItsPictureEnds()
    {
        Assert.False(LibavVideoFramePlayer.ResolveLoop(new PlaybackLoop(0, double.NaN), 0).CanLoop);
    }
}
