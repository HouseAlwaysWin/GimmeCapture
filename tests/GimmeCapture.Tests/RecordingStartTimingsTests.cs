using System.Text.RegularExpressions;
using System.Threading;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

/// <summary>
/// The <c>Recording.StartTimings</c> line is how a slow start reported from the field is traced to a stage, so its shape
/// is pinned: each stage the time since the one before it, the total, then the figures that are not stages.
/// </summary>
public class RecordingStartTimingsTests
{
    [Fact]
    public void EachStage_ReportsTheTimeSinceTheOneBefore_ThenTheTotal_ThenTheNotes()
    {
        var timings = new RecordingStartTimings("n=1 kind=start");
        timings.Mark("ffmpeg");
        Thread.Sleep(30);
        timings.Mark("video");
        timings.Note("encoder", "libx264");
        timings.Note("encoderOpen", TimeSpan.FromMilliseconds(12.4));

        string line = timings.Finish();

        var match = Regex.Match(line, @"^n=1 kind=start ffmpeg=(\d+) video=(\d+) total=(\d+)ms encoder=libx264 encoderOpen=12$");
        Assert.True(match.Success, line);
        int ffmpeg = int.Parse(match.Groups[1].Value);
        int video = int.Parse(match.Groups[2].Value);
        int total = int.Parse(match.Groups[3].Value);
        Assert.True(video >= 20, $"video={video} should cover the sleep before it");
        Assert.True(total >= ffmpeg + video - 1, line); // each figure is rounded on its own
    }
}
