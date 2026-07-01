using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

// Unit-level checks for the multi-segment clip-trim view model: split at the playhead, keep/drop, and the
// resulting kept runs. Reuses the shared VideoSegmentEditor engine. The actual decode/playback is covered
// by the gated real-encode integration path.
public class TrimViewModelTests
{
    private static TrimViewModel Make(
        double duration = 100, IReadOnlyList<VideoEditSegment>? kept = null,
        Action<IReadOnlyList<VideoEditSegment>>? onApply = null)
        => new(@"C:\does-not-exist.mp4", duration, 30, 1920, 1080, 0,
               kept ?? new[] { new VideoEditSegment(0, duration) },
               onApply ?? (_ => { }));

    [Fact]
    public void Constructor_WholeClip_IsOneKeptRun()
    {
        using var vm = Make(100);

        Assert.Single(vm.EditSegments);
        Assert.True(vm.EditSegments[0].Kept);
        Assert.Single(vm.KeptRuns());
        Assert.Equal(0d, vm.KeptRuns()[0].SourceStart);
        Assert.Equal(100d, vm.KeptRuns()[0].SourceEnd);
        Assert.Equal(0d, vm.PositionSeconds);
        Assert.True(vm.IsPreparing);
    }

    [Fact]
    public void Split_AtPlayhead_MakesTwoPieces()
    {
        using var vm = Make(100);
        vm.PositionSeconds = 40;

        vm.Split();

        Assert.Equal(2, vm.EditSegments.Count);
        Assert.Equal(40d, vm.EditSegments[0].SourceEnd);
        Assert.Equal(40d, vm.EditSegments[1].SourceStart);
        // Both kept + contiguous → the kept runs coalesce back to a single run.
        Assert.Single(vm.KeptRuns());
    }

    [Fact]
    public void Split_ThenDropFirst_KeepsSecondRun()
    {
        using var vm = Make(100);
        vm.PositionSeconds = 40;
        vm.Split();

        vm.ToggleKept(0); // drop [0,40]

        Assert.Single(vm.KeptRuns());
        Assert.Equal(40d, vm.KeptRuns()[0].SourceStart);
        Assert.Equal(100d, vm.KeptRuns()[0].SourceEnd);
    }

    [Fact]
    public void DroppingTheLastKeptPiece_IsIgnored()
    {
        using var vm = Make(100);

        vm.ToggleKept(0); // only piece — must stay kept

        Assert.Single(vm.KeptRuns());
    }

    [Fact]
    public void TwoCuts_DropMiddle_YieldsTwoConcatenatedRuns()
    {
        using var vm = Make(100);
        vm.PositionSeconds = 30;
        vm.Split(); // [0,30] [30,100]
        vm.PositionSeconds = 60;
        vm.Split(); // [0,30] [30,60] [60,100]

        vm.ToggleKept(1); // drop [30,60]

        var runs = vm.KeptRuns();
        Assert.Equal(2, runs.Length);
        Assert.Equal(0d, runs[0].SourceStart);
        Assert.Equal(30d, runs[0].SourceEnd);
        Assert.Equal(60d, runs[1].SourceStart);
        Assert.Equal(100d, runs[1].SourceEnd);
    }

    [Fact]
    public void Apply_InvokesCallback_WithKeptRuns()
    {
        IReadOnlyList<VideoEditSegment>? got = null;
        using var vm = Make(100, onApply: runs => got = runs);
        vm.PositionSeconds = 50;
        vm.Split();
        vm.ToggleKept(0); // drop [0,50] → keep [50,100]

        vm.Apply();

        Assert.NotNull(got);
        Assert.Single(got!);
        Assert.Equal(50d, got![0].SourceStart);
        Assert.Equal(100d, got[0].SourceEnd);
    }

    [Fact]
    public async Task SeekAndScrub_BeforeFrame_DoNotThrow()
    {
        using var vm = Make(100);

        await vm.SeekAsync(5);   // unreadable file -> decode returns null / is caught, no throw
        await vm.SeekAsync(-3);  // clamps without throwing
        vm.BeginScrub();

        Assert.False(vm.IsPlaying);
    }
}
