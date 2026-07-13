using System.Collections.Generic;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

// Per-file kept-runs resolution on CompressQueueItem (EffectiveKeptRuns / EffectiveDuration). Pure logic against
// the probed duration; the source file need not exist (ReadFileMeta is best-effort).
public class CompressTrimTests
{
    private static CompressQueueItem Item(double probedDuration) =>
        new(@"D:\Videos\clip.mp4") { ProbedDuration = probedDuration };

    [Fact]
    public void Trim_Disabled_ReturnsWholeClip()
    {
        CompressQueueItem item = Item(100);

        Assert.False(item.TrimEnabled);
        var runs = item.EffectiveKeptRuns();
        Assert.Single(runs);
        Assert.Equal(0d, runs[0].Start);
        Assert.Equal(100d, runs[0].End);
        Assert.Equal(100d, item.EffectiveDuration);
    }

    [Fact]
    public void Trim_Enabled_WithSegments_ReturnsClampedSortedRuns()
    {
        CompressQueueItem item = Item(100);
        item.TrimEnabled = true;
        item.KeptSegments = new[] { new VideoEditSegment(10, 30), new VideoEditSegment(50, 120) };

        var runs = item.EffectiveKeptRuns();
        Assert.Equal(2, runs.Count);
        Assert.Equal(10d, runs[0].Start);
        Assert.Equal(30d, runs[0].End);
        Assert.Equal(50d, runs[1].Start);
        Assert.Equal(100d, runs[1].End); // clamped to the probed duration
        Assert.Equal(70d, item.EffectiveDuration); // 20 + 50
    }

    [Fact]
    public void Trim_Enabled_EmptySegments_FallsBackToWholeClip()
    {
        CompressQueueItem item = Item(100);
        item.TrimEnabled = true;
        item.KeptSegments = new List<VideoEditSegment>();

        Assert.Single(item.EffectiveKeptRuns());
        Assert.Equal(100d, item.EffectiveDuration);
    }

    [Fact]
    public void Trim_BeforeProbe_ReturnsEmpty()
    {
        CompressQueueItem item = Item(0);
        item.TrimEnabled = true;
        item.KeptSegments = new[] { new VideoEditSegment(10, 30) };

        Assert.Empty(item.EffectiveKeptRuns());
        Assert.Equal(0d, item.EffectiveDuration);
    }

    [Fact]
    public void Speed_PreservedInRuns_AndShortensOutputDuration()
    {
        CompressQueueItem item = Item(100);
        item.TrimEnabled = true;
        item.KeptSegments = new[] { new VideoEditSegment(0, 100, 2.0) };

        var runs = item.EffectiveKeptRuns();
        Assert.Single(runs);
        Assert.Equal(2.0, runs[0].Speed, 3);
        Assert.Equal(100d, item.EffectiveDuration);          // source span unchanged
        Assert.Equal(50d, item.EffectiveOutputDuration, 3);  // 100s at 2× = 50s output
    }

    [Fact]
    public void HasEdits_FalseForPlainClip_TrueWithCropOrRotation()
    {
        CompressQueueItem item = Item(100);
        Assert.False(item.HasEdits);

        item.Rotation = 90;
        Assert.True(item.HasEdits);
        item.Rotation = 0;

        item.Crop = new VideoEditCrop(0, 0, 640, 480);
        Assert.True(item.HasEdits);
    }
}
