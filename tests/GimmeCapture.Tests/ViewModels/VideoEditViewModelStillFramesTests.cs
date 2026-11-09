using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// How long the Compress editor keeps its video open between paused frames. The user chose five minutes over the 30 s
/// the decoder closes after by default: the first frame after a close takes 0.4-0.6 s on a 4K video, a step 0.03-0.18.
/// </summary>
public sealed class VideoEditViewModelStillFramesTests
{
    [Fact]
    public void AnOpenEditor_KeepsItsVideoOpenForFiveMinutesWithoutAFrame()
    {
        using var vm = new VideoEditViewModel(
            "not-opened-until-a-frame-is-asked-for.mp4", 10, 30, 640, 360,
            VideoEditResult.Empty with { KeptRuns = [new VideoEditSegment(0, 10)] }, _ => { });

        Assert.Equal(TimeSpan.FromMinutes(5), vm.StillFramesKeptOpenFor);
    }

    [Fact]
    public void AnArrowKey_MovesTheTimeAFrame_AndStopsAtBothEnds()
    {
        using var vm = NewEditor();

        vm.PositionSeconds = 5;
        vm.StepFrameByKey(1);
        Assert.Equal(5 + (1.0 / 30), vm.PositionSeconds, 6);
        vm.StepFrameByKey(-1);
        Assert.Equal(5, vm.PositionSeconds, 6);

        vm.PositionSeconds = 0;
        vm.StepFrameByKey(-1);
        Assert.Equal(0, vm.PositionSeconds);

        vm.PositionSeconds = 10;
        vm.StepFrameByKey(1);
        Assert.Equal(10, vm.PositionSeconds);
    }

    [Fact]
    public async Task TheFirstFrame_IsUnderWayBeforeTheWindowAsksForIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string clip = VideoTestClips.WriteConstantRate(Path.Combine(dir, "clip.mp4"), 64, 48, 30, 30, gopSize: 10);
            using var vm = new VideoEditViewModel(
                clip, 1, 30, 64, 48, VideoEditResult.Empty with { KeptRuns = [new VideoEditSegment(0, 1)] }, _ => { });
            Assert.False(vm.StillFramesAreOpen);

            vm.StartDecodingFirstFrame();
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (!vm.StillFramesAreOpen)
            {
                Assert.True(DateTime.UtcNow < deadline, "Nothing began on the first frame.");
                await OwnThread.Delay(TimeSpan.FromMilliseconds(5));
            }

            await vm.InitializeAsync(); // what the window does once it has opened
            Assert.False(vm.IsPreparing);
            Assert.Equal(1, vm.StillFramesOpens);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheKeys_WaitForTheFirstFrame()
    {
        using var vm = NewEditor();
        Assert.False(vm.TakesTransportKeys);

        await vm.InitializeAsync(); // nothing to decode here, and the editor is ready all the same
        Assert.True(vm.TakesTransportKeys);
    }

    private static VideoEditViewModel NewEditor() => new(
        "not-opened-until-a-frame-is-asked-for.mp4", 10, 30, 640, 360,
        VideoEditResult.Empty with { KeptRuns = [new VideoEditSegment(0, 10)] }, _ => { });
}
