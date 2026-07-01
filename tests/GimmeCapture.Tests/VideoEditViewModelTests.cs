using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

// Unit-level checks for the compress "進階影片編輯" view model: split / merge / keep-drop, per-segment speed,
// crop, rotation, and the resulting kept runs + Apply payload. Reuses the shared VideoSegmentEditor engine;
// the actual decode/playback is covered by the gated real-encode integration path.
public class VideoEditViewModelTests
{
    private static VideoEditViewModel Make(
        double duration = 100, IReadOnlyList<VideoEditSegment>? kept = null, VideoEditCrop? crop = null,
        int rotation = 0, Action<IReadOnlyList<VideoEditSegment>, VideoEditCrop?, int>? onApply = null)
        => new(@"C:\does-not-exist.mp4", duration, 30, 1920, 1080, rotation,
               kept ?? new[] { new VideoEditSegment(0, duration) }, crop,
               onApply ?? ((_, _, _) => { }));

    [Fact]
    public void Constructor_WholeClip_IsOneKeptRun()
    {
        using var vm = Make(100);

        Assert.Single(vm.EditSegments);
        Assert.True(vm.EditSegments[0].Kept);
        Assert.Single(vm.KeptRuns());
        Assert.Equal(0d, vm.KeptRuns()[0].SourceStart);
        Assert.Equal(100d, vm.KeptRuns()[0].SourceEnd);
        Assert.Equal(0, vm.RotationDegrees);
        Assert.False(vm.HasCrop);
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
    public void Merge_AfterDrop_RestoresCutContent()
    {
        using var vm = Make(100);
        vm.PositionSeconds = 40;
        vm.Split();
        vm.ToggleKept(0); // drop [0,40] → keep [40,100]

        vm.PositionSeconds = 40;
        vm.Merge(); // un-cut

        Assert.Single(vm.EditSegments);
        Assert.True(vm.EditSegments[0].Kept);
        Assert.Single(vm.KeptRuns());
        Assert.Equal(0d, vm.KeptRuns()[0].SourceStart);
        Assert.Equal(100d, vm.KeptRuns()[0].SourceEnd);
    }

    [Fact]
    public void CycleSpeed_CyclesThePieceUnderThePlayhead()
    {
        using var vm = Make(100);
        vm.PositionSeconds = 50;

        vm.CycleSpeed(); // 1 → 1.5
        Assert.Equal(1.5, vm.EditSegments[0].Speed, 3);
        vm.CycleSpeed(); // 1.5 → 2
        vm.CycleSpeed(); // 2 → 0.5
        Assert.Equal(0.5, vm.EditSegments[0].Speed, 3);
    }

    [Fact]
    public void Speed_ShortensKeptRunOutputDuration()
    {
        using var vm = Make(100);
        vm.PositionSeconds = 50;
        vm.CycleSpeed(); // whole clip → 1.5×

        Assert.Equal(100.0 / 1.5, vm.KeptRuns().Sum(r => r.OutputDuration), 1); // ≈ 66.7s output
    }

    [Fact]
    public void Merge_AcrossSpeedSeam_IsRefused_PreservingBothSpeeds()
    {
        using var vm = Make(100);
        vm.PositionSeconds = 50;
        vm.Split();          // [0,50] [50,100], both 1×
        vm.PositionSeconds = 75;
        vm.CycleSpeed();     // [50,100] → 1.5×

        vm.PositionSeconds = 50;
        vm.Merge();          // the seam is a genuine speed boundary → refused (no silent flatten)

        Assert.Equal(2, vm.EditSegments.Count);
        Assert.Equal(1.0, vm.EditSegments[0].Speed, 3);
        Assert.Equal(1.5, vm.EditSegments[1].Speed, 3);
    }

    [Fact]
    public void Rotate_CyclesBy90()
    {
        using var vm = Make(100);

        vm.Rotate(); Assert.Equal(90, vm.RotationDegrees);
        vm.Rotate(); vm.Rotate(); Assert.Equal(270, vm.RotationDegrees);
        vm.Rotate(); Assert.Equal(0, vm.RotationDegrees);
    }

    [Fact]
    public void SetCropFromSelection_MapsToSourcePixels_AndClearResets()
    {
        using var vm = Make(100); // source 1920×1080

        // Left-half selection on a 960×540 display (2× scale) → source (0,0,960,540).
        vm.SetCropFromSelection(0, 0, 480, 270, 960, 540);

        Assert.True(vm.HasCrop);
        Assert.Equal(0, vm.Crop!.X);
        Assert.Equal(960, vm.Crop.Width);
        Assert.Equal(540, vm.Crop.Height);

        vm.ClearCrop();
        Assert.False(vm.HasCrop);
    }

    [Fact]
    public void Apply_ReturnsKeptRuns_Crop_AndRotation()
    {
        IReadOnlyList<VideoEditSegment>? gotRuns = null;
        VideoEditCrop? gotCrop = null;
        int gotRotation = -1;
        using var vm = Make(100, rotation: 90,
            onApply: (runs, crop, rot) => { gotRuns = runs; gotCrop = crop; gotRotation = rot; });
        vm.PositionSeconds = 50;
        vm.Split();
        vm.ToggleKept(0); // keep [50,100]
        vm.SetCropFromSelection(0, 0, 480, 270, 960, 540);
        vm.Rotate(); // 90 → 180

        vm.Apply();

        Assert.NotNull(gotRuns);
        Assert.Single(gotRuns!);
        Assert.Equal(50d, gotRuns![0].SourceStart);
        Assert.Equal(100d, gotRuns[0].SourceEnd);
        Assert.NotNull(gotCrop);
        Assert.Equal(180, gotRotation);
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
