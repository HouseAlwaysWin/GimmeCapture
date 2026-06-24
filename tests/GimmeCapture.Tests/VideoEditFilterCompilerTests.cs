using System.Collections.Generic;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

public class VideoEditFilterCompilerTests
{
    private static VideoEditProject Project(
        IReadOnlyList<VideoEditSegment> segments,
        double total,
        VideoEditCrop? crop = null,
        IReadOnlyList<VideoEditOverlay>? overlays = null,
        VideoEditAudio? audio = null) =>
        new()
        {
            Segments = segments,
            TotalSourceDuration = total,
            Crop = crop,
            Overlays = overlays ?? System.Array.Empty<VideoEditOverlay>(),
            Audio = audio,
        };

    [Fact]
    public void SingleFullSegment_NoAudio_ProducesPlainTrimSetpts()
    {
        var p = Project(new[] { new VideoEditSegment(0, 10) }, total: 10);

        var c = VideoEditFilterCompiler.Compile(p, sourceHasAudio: false);

        Assert.Equal("[0:v]trim=start=0:end=10,setpts=(PTS-STARTPTS)[v0]", c.FilterComplex);
        Assert.Equal("[v0]", c.VideoMap);
        Assert.Null(c.AudioMap);
        Assert.Equal(0, c.OverlayInputCount);
    }

    [Fact]
    public void CutMiddle_TwoSegments_ConcatsVideoAndAudio()
    {
        var p = Project(
            new[] { new VideoEditSegment(0, 3), new VideoEditSegment(7, 10) },
            total: 10);

        var c = VideoEditFilterCompiler.Compile(p, sourceHasAudio: true);

        Assert.Contains("[0:v]trim=start=0:end=3,setpts=(PTS-STARTPTS)[v0];", c.FilterComplex);
        Assert.Contains("[0:v]trim=start=7:end=10,setpts=(PTS-STARTPTS)[v1];", c.FilterComplex);
        Assert.Contains("[v0][v1]concat=n=2:v=1:a=0[vcat];", c.FilterComplex);
        Assert.Contains("[a0][a1]concat=n=2:v=0:a=1[acat];", c.FilterComplex);
        Assert.Equal("[vcat]", c.VideoMap);
        Assert.Equal("[acat]", c.AudioMap);
    }

    [Fact]
    public void Speed2x_UsesSetptsDivideAndAtempo()
    {
        var p = Project(new[] { new VideoEditSegment(0, 10, Speed: 2.0) }, total: 10);

        var c = VideoEditFilterCompiler.Compile(p, sourceHasAudio: true);

        Assert.Contains("setpts=(PTS-STARTPTS)/2[v0]", c.FilterComplex);
        Assert.Contains("asetpts=(PTS-STARTPTS),atempo=2[a0]", c.FilterComplex);
    }

    [Fact]
    public void Speed4x_ChainsAtempo()
    {
        var p = Project(new[] { new VideoEditSegment(0, 10, Speed: 4.0) }, total: 10);

        var c = VideoEditFilterCompiler.Compile(p, sourceHasAudio: true);

        Assert.Contains("atempo=2.0,atempo=2[a0]", c.FilterComplex);
    }

    [Fact]
    public void SlowMotion_HalfSpeed_UsesSingleAtempo()
    {
        var p = Project(new[] { new VideoEditSegment(0, 10, Speed: 0.5) }, total: 10);

        var c = VideoEditFilterCompiler.Compile(p, sourceHasAudio: true);

        Assert.Contains("setpts=(PTS-STARTPTS)/0.5[v0]", c.FilterComplex);
        Assert.Contains("atempo=0.5[a0]", c.FilterComplex);
    }

    [Fact]
    public void Crop_AppendsCropFilterAndRemapsVideo()
    {
        var p = Project(new[] { new VideoEditSegment(0, 10) }, total: 10, crop: new VideoEditCrop(10, 20, 100, 50));

        var c = VideoEditFilterCompiler.Compile(p, sourceHasAudio: false);

        Assert.Contains("crop=100:50:10:20[vcrop]", c.FilterComplex);
        Assert.Equal("[vcrop]", c.VideoMap);
    }

    [Fact]
    public void TimedOverlay_EmitsEnableBetweenAndCountsInput()
    {
        var p = Project(
            new[] { new VideoEditSegment(0, 10) },
            total: 10,
            overlays: new[] { new VideoEditOverlay("o.png", X: 5, Y: 6, StartSeconds: 2, EndSeconds: 4) });

        var c = VideoEditFilterCompiler.Compile(p, sourceHasAudio: false);

        Assert.Contains("[v0][1:v]overlay=5:6:enable='between(t,2,4)'[ov0]", c.FilterComplex);
        Assert.Equal("[ov0]", c.VideoMap);
        Assert.Equal(1, c.OverlayInputCount);
    }

    [Fact]
    public void Audio_VolumeFadeAndMute_AppendsFilters()
    {
        var p = Project(
            new[] { new VideoEditSegment(0, 10) },
            total: 10,
            audio: new VideoEditAudio(
                Volume: 0.5,
                FadeInSeconds: 1,
                FadeOutSeconds: 2,
                MutedRanges: new[] { new VideoEditMuteRange(3, 4) }));

        var c = VideoEditFilterCompiler.Compile(p, sourceHasAudio: true);

        Assert.Contains("volume=0.5", c.FilterComplex);
        Assert.Contains("afade=t=in:st=0:d=1", c.FilterComplex);
        Assert.Contains("afade=t=out:st=8:d=2", c.FilterComplex); // 10 - 2
        Assert.Contains("volume=enable='between(t,3,4)':volume=0", c.FilterComplex);
        Assert.Equal("[aout]", c.AudioMap);
    }

    [Fact]
    public void NoSegments_DefaultsToWholeClip()
    {
        var p = Project(System.Array.Empty<VideoEditSegment>(), total: 12);

        var c = VideoEditFilterCompiler.Compile(p, sourceHasAudio: false);

        Assert.Contains("trim=start=0:end=12", c.FilterComplex);
    }

    [Fact]
    public void IsTrivial_DistinguishesNoOpFromEdits()
    {
        Assert.True(Project(new[] { new VideoEditSegment(0, 10) }, total: 10).IsTrivial);
        Assert.False(Project(new[] { new VideoEditSegment(0, 3), new VideoEditSegment(7, 10) }, total: 10).IsTrivial);
        Assert.False(Project(new[] { new VideoEditSegment(0, 10, Speed: 2.0) }, total: 10).IsTrivial);
        Assert.False(Project(new[] { new VideoEditSegment(0, 10) }, total: 10, crop: new VideoEditCrop(0, 0, 4, 4)).IsTrivial);
    }
}
