using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit;

namespace GimmeCapture.Tests;

// Covers the pure real-time catch-up decision used by LibavVideoFramePlayer's playback loop: a frame already
// well behind its scheduled wall-clock slot is decoded but not displayed, so playback stays in sync instead of
// piling late frames onto the UI thread — with a hard cap so it never stops rendering entirely.
public class LibavVideoFramePlayerTests
{
    [Fact]
    public void OnTimeFrame_IsNotDropped()
    {
        // Wall clock exactly matches the frame's scheduled time → render it.
        Assert.False(LibavVideoFramePlayer.ShouldDropLateFrame(1.0, 0.0, 1.0, 1.0, 0));
    }

    [Fact]
    public void FirstFrame_IsNotDropped()
    {
        Assert.False(LibavVideoFramePlayer.ShouldDropLateFrame(0.0, 0.0, 0.0, 1.0, 0));
    }

    [Fact]
    public void SlightlyLateFrame_WithinThreshold_IsNotDropped()
    {
        // 50 ms behind schedule (< 100 ms threshold) → still displayed.
        Assert.False(LibavVideoFramePlayer.ShouldDropLateFrame(1.05, 0.0, 1.0, 1.0, 0));
    }

    [Fact]
    public void BadlyLateFrame_BeyondThreshold_IsDropped()
    {
        // 300 ms behind schedule → skip display and decode ahead to catch up.
        Assert.True(LibavVideoFramePlayer.ShouldDropLateFrame(1.30, 0.0, 1.0, 1.0, 0));
    }

    [Fact]
    public void ConsecutiveDropCap_ForcesRender_EvenWhenVeryLate()
    {
        // Already dropped the max in a row → render regardless of how far behind, so the picture keeps advancing.
        Assert.False(LibavVideoFramePlayer.ShouldDropLateFrame(10.0, 0.0, 1.0, 1.0, 4));
        Assert.False(LibavVideoFramePlayer.ShouldDropLateFrame(10.0, 0.0, 1.0, 1.0, 99));
    }

    [Fact]
    public void SpeedScaling_ShrinksScheduledWallTime()
    {
        // At 2× speed a source-time-2.0s frame is scheduled at wall 1.0s. At wall 1.0s it is on time (not dropped);
        // well past that it is dropped.
        Assert.False(LibavVideoFramePlayer.ShouldDropLateFrame(1.0, 0.0, 2.0, 2.0, 0));
        Assert.True(LibavVideoFramePlayer.ShouldDropLateFrame(1.5, 0.0, 2.0, 2.0, 0));
    }
}
