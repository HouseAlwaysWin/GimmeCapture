using System.Threading;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// How a region recording chooses between Desktop Duplication and the screen grab, and what it does when Windows takes
/// a duplication away mid-recording (a display mode change, the secure desktop of a UAC prompt): it opens a fresh one,
/// or carries on with the screen grab when a fresh one fails before its first frame. Stand-in sources, no FFmpeg.
/// </summary>
public sealed unsafe class RegionCaptureSourceTests
{
    [Fact]
    public void ADuplicationThatOpens_IsUsed_AndTheScreenGrabIsNotOpened()
    {
        var duplication = new StandIn("ddagrab", frames: 3);
        int screenGrabs = 0;

        using var capture = new RegionCaptureSource(() => duplication, () => { screenGrabs++; return new StandIn("gdigrab"); });

        Assert.Equal("ddagrab", capture.Name);
        Assert.Equal(3, ReadAll(capture));
        Assert.Equal(0, screenGrabs);
    }

    [Fact]
    public void WhereDuplicationDoesNotOpen_TheScreenGrabRecords()
    {
        using var capture = new RegionCaptureSource(() => null, () => new StandIn("gdigrab", frames: 2));

        Assert.Equal("gdigrab", capture.Name);
        Assert.Equal(2, ReadAll(capture));
    }

    [Fact]
    public void ADuplicationLostAfterGivingFrames_IsOpenedAgain_AndTheRecordingCarriesOn()
    {
        var first = new StandIn("ddagrab", frames: 3, thenFails: true);
        var second = new StandIn("ddagrab", frames: 2);
        var opened = new Queue<StandIn>(new[] { first, second });
        int screenGrabs = 0;

        using var capture = new RegionCaptureSource(() => opened.Dequeue(), () => { screenGrabs++; return new StandIn("gdigrab"); });

        Assert.Equal(5, ReadAll(capture));
        Assert.True(first.Disposed);
        Assert.Equal(0, screenGrabs);
    }

    [Fact]
    public void AFreshDuplicationThatFailsBeforeItsFirstFrame_GivesWayToTheScreenGrab()
    {
        var first = new StandIn("ddagrab", frames: 2, thenFails: true);
        var second = new StandIn("ddagrab", frames: 0, thenFails: true);
        var opened = new Queue<StandIn>(new[] { first, second });
        var screenGrab = new StandIn("gdigrab", frames: 4);

        using var capture = new RegionCaptureSource(() => opened.Dequeue(), () => screenGrab);

        Assert.Equal(6, ReadAll(capture));
        Assert.Equal("gdigrab", capture.Name);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
    }

    [Fact]
    public void ADuplicationThatDoesNotOpenAgain_GivesWayToTheScreenGrab()
    {
        var first = new StandIn("ddagrab", frames: 1, thenFails: true);
        bool openedOnce = false;

        using var capture = new RegionCaptureSource(
            () => { if (openedOnce) { return null; } openedOnce = true; return first; },
            () => new StandIn("gdigrab", frames: 2));

        Assert.Equal(3, ReadAll(capture));
        Assert.Equal("gdigrab", capture.Name);
    }

    [Fact]
    public void AScreenGrabFailure_FailsTheRecording()
    {
        using var capture = new RegionCaptureSource(() => null, () => new StandIn("gdigrab", frames: 1, thenFails: true));

        Assert.True(capture.TryRead(null, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => capture.TryRead(null, CancellationToken.None));
    }

    [Fact]
    public void Disposing_LetsGoOfTheSourceInUse()
    {
        var duplication = new StandIn("ddagrab", frames: 1);
        var capture = new RegionCaptureSource(() => duplication, () => new StandIn("gdigrab"));

        capture.Dispose();

        Assert.True(duplication.Disposed);
    }

    private static int ReadAll(RegionCaptureSource capture)
    {
        int frames = 0;
        while (capture.TryRead(null, CancellationToken.None))
        {
            frames++;
        }

        return frames;
    }

    // Gives a number of frames, then ends or fails.
    private sealed class StandIn(string name, int frames = 0, bool thenFails = false) : ICapturedFrameSource
    {
        private int _left = frames;

        public string Name => name;

        public bool Disposed { get; private set; }

        public bool TryRead(AVFrame* frame, CancellationToken ct)
        {
            Assert.False(Disposed, "read after it was disposed");
            if (_left > 0)
            {
                _left--;
                return true;
            }

            return thenFails ? throw new InvalidOperationException($"{name} lost") : false;
        }

        public void Dispose() => Disposed = true;
    }
}
