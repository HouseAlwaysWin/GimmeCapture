using System.Collections.Generic;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

public class VideoSegmentEditorTests
{
    [Fact]
    public void FromTrim_ProducesSingleSegmentAndClamps()
    {
        var segs = VideoSegmentEditor.FromTrim(trimStart: -5, trimEnd: 0, totalDuration: 12);

        Assert.Single(segs);
        Assert.Equal(0, segs[0].SourceStart);
        Assert.Equal(12, segs[0].SourceEnd); // end<=start => falls back to total
    }

    [Fact]
    public void SegmentOutputStart_IsCumulativeOutputDuration()
    {
        var segs = new[]
        {
            new VideoEditSegment(0, 3),   // 3s out
            new VideoEditSegment(7, 10),  // 3s out
        };

        Assert.Equal(0, VideoSegmentEditor.SegmentOutputStart(segs, 0));
        Assert.Equal(3, VideoSegmentEditor.SegmentOutputStart(segs, 1));
        Assert.Equal(6, VideoSegmentEditor.SegmentOutputStart(segs, 2)); // == total output
    }

    [Fact]
    public void TryMapOutputToSource_MapsAcrossACutGap()
    {
        var segs = new[]
        {
            new VideoEditSegment(0, 3),   // output [0,3) -> source [0,3)
            new VideoEditSegment(7, 10),  // output [3,6) -> source [7,10)
        };

        Assert.True(VideoSegmentEditor.TryMapOutputToSource(segs, 1.0, out int i0, out double s0));
        Assert.Equal(0, i0);
        Assert.Equal(1.0, s0, 3);

        // 4s of output is 1s into the second segment -> source 8s (the cut middle is skipped).
        Assert.True(VideoSegmentEditor.TryMapOutputToSource(segs, 4.0, out int i1, out double s1));
        Assert.Equal(1, i1);
        Assert.Equal(8.0, s1, 3);
    }

    [Fact]
    public void TryMapOutputToSource_HonorsSpeed()
    {
        var segs = new[] { new VideoEditSegment(0, 10, Speed: 2.0) }; // 5s output

        // 2s of (2x) output == 4s of source.
        Assert.True(VideoSegmentEditor.TryMapOutputToSource(segs, 2.0, out _, out double src));
        Assert.Equal(4.0, src, 3);
    }

    [Fact]
    public void TryMapOutputToSource_ClampsAndHandlesEmpty()
    {
        var segs = new[] { new VideoEditSegment(2, 6) };

        Assert.True(VideoSegmentEditor.TryMapOutputToSource(segs, -1, out _, out double before));
        Assert.Equal(2, before, 3); // negative clamps to first start

        Assert.True(VideoSegmentEditor.TryMapOutputToSource(segs, 999, out _, out double after));
        Assert.Equal(6, after, 3); // past end clamps to last end

        Assert.False(VideoSegmentEditor.TryMapOutputToSource(System.Array.Empty<VideoEditSegment>(), 1, out _, out _));
    }

    [Fact]
    public void SplitAtOutputTime_SplitsSegmentAtSourcePoint()
    {
        var segs = new[] { new VideoEditSegment(0, 10) };

        var result = VideoSegmentEditor.SplitAtOutputTime(segs, 4.0);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].SourceStart);
        Assert.Equal(4, result[0].SourceEnd);
        Assert.Equal(4, result[1].SourceStart);
        Assert.Equal(10, result[1].SourceEnd);
    }

    [Fact]
    public void SplitAtOutputTime_HonorsSpeedForSourcePoint()
    {
        var segs = new[] { new VideoEditSegment(0, 10, Speed: 2.0) }; // 5s output

        var result = VideoSegmentEditor.SplitAtOutputTime(segs, 2.0); // 2s out -> 4s source

        Assert.Equal(2, result.Count);
        Assert.Equal(4, result[0].SourceEnd, 3);
        Assert.Equal(4, result[1].SourceStart, 3);
    }

    [Fact]
    public void SplitAtOutputTime_AtBoundaryIsNoOp()
    {
        var segs = new[] { new VideoEditSegment(0, 3), new VideoEditSegment(7, 10) };

        // 3.0 is exactly the seam between the two segments -> nothing to split.
        var result = VideoSegmentEditor.SplitAtOutputTime(segs, 3.0);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SplitAtSourceTime_SplitsContainingSegment()
    {
        var segs = new[] { new VideoEditSegment(0, 3), new VideoEditSegment(7, 10) };

        // 8s source falls inside the second kept segment [7,10) -> split there.
        var result = VideoSegmentEditor.SplitAtSourceTime(segs, 8.0);

        Assert.Equal(3, result.Count);
        Assert.Equal(7, result[1].SourceStart);
        Assert.Equal(8, result[1].SourceEnd);
        Assert.Equal(8, result[2].SourceStart);
        Assert.Equal(10, result[2].SourceEnd);
    }

    [Fact]
    public void SplitAtSourceTime_InsideCutGapIsNoOp()
    {
        var segs = new[] { new VideoEditSegment(0, 3), new VideoEditSegment(7, 10) };

        // 5s source is in the removed gap [3,7) -> nothing to split.
        var result = VideoSegmentEditor.SplitAtSourceTime(segs, 5.0);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void RemoveAt_RemovesSegment()
    {
        var segs = new[]
        {
            new VideoEditSegment(0, 3),
            new VideoEditSegment(3, 6),
            new VideoEditSegment(6, 9),
        };

        var result = VideoSegmentEditor.RemoveAt(segs, 1);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, result[0].SourceStart);
        Assert.Equal(6, result[1].SourceStart);
    }

    [Fact]
    public void RemoveAt_KeepsAtLeastOneSegment()
    {
        var segs = new[] { new VideoEditSegment(0, 3) };

        var result = VideoSegmentEditor.RemoveAt(segs, 0);

        Assert.Single(result); // never delete the last remaining segment
    }

    [Fact]
    public void RemoveAt_OutOfRangeIsCopy()
    {
        var segs = new[] { new VideoEditSegment(0, 3), new VideoEditSegment(3, 6) };

        var result = VideoSegmentEditor.RemoveAt(segs, 5);

        Assert.Equal(2, result.Count);
    }
}
