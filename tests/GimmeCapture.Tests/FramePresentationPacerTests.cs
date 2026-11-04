using System;
using System.Diagnostics;
using GimmeCapture.Services.Core.Media;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// Which decoded frames a video pin hands to the UI thread (<see cref="FramePresentationPacer"/>), at the default
/// 30 frames a second. A 30 fps recording showed about half its frames in its pin (194 of 367), as the old check read a
/// tick count that moves in 15.6 ms steps; a clip no faster than the UI must show every frame, a faster one an even
/// share.
/// </summary>
public class FramePresentationPacerTests
{
    private static readonly TimeSpan UiInterval = TimeSpan.FromSeconds(1.0 / 30);

    private static long Ticks(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000);

    // How many of `frames` frames, one every `frameMs` (plus each one's own lateness), go to the UI.
    private static int Passed(double frameMs, int frames, Func<int, double>? lateMs = null)
    {
        var pacer = new FramePresentationPacer(UiInterval);
        int passed = 0;
        for (int i = 0; i < frames; i++)
        {
            if (pacer.TryPass(Ticks(1_000 + (i * frameMs) + (lateMs?.Invoke(i) ?? 0))))
            {
                passed++;
            }
        }

        return passed;
    }

    [Fact]
    public void AClipAsFastAsTheUi_ShowsEveryFrame()
    {
        Assert.Equal(300, Passed(1000.0 / 30, 300));
    }

    [Fact]
    public void AClipAsFastAsTheUi_ShowsEveryFrame_ThoughEachIsAFewMillisecondsLateOrEarly()
    {
        Assert.Equal(300, Passed(1000.0 / 30, 300, i => (i * 7 % 5) - 2));
    }

    [Fact]
    public void AFrameLateByTenMilliseconds_DoesNotCostTheOnTimeOneAfterIt()
    {
        Assert.Equal(300, Passed(1000.0 / 30, 300, i => i % 10 == 3 ? 10 : 0));
    }

    [Theory]
    [InlineData(60.0, 300, 151)]  // one frame in two, after the first two
    [InlineData(120.0, 600, 151)] // one in four
    [InlineData(40.0, 400, 300)]  // three in four: 30 a second all the same
    [InlineData(24.0, 240, 240)]  // slower than the UI: all of them
    public void AFasterClip_ShowsAnEvenShare_ThirtyASecond(double clipFps, int frames, int expected)
    {
        Assert.InRange(Passed(1000.0 / clipFps, frames), expected - 1, expected + 1);
    }

    [Fact]
    public void AfterAStall_ThePictureCatchesUp_WithoutABurst()
    {
        var pacer = new FramePresentationPacer(UiInterval);
        Assert.True(pacer.TryPass(Ticks(1_000)));

        // Half a second without a frame, then frames at 120 fps: one shows at once, then a quarter of them.
        int passed = 0;
        for (int i = 0; i < 8; i++)
        {
            if (pacer.TryPass(Ticks(1_500 + (i * 1000.0 / 120))))
            {
                passed++;
            }
        }

        Assert.InRange(passed, 2, 3);
    }
}
