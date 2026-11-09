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
}
