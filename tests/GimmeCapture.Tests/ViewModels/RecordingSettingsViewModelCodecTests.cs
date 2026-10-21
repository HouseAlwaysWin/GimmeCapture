using System.Linq;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// The codec list used to run the hardware AV1 probe — loading FFmpeg first — the moment settings touched it, on the
/// UI thread at startup (1.75 s measured). Now it only reads the probe's answer, and the probe runs on the pool and
/// reports back. These pin down what the list does before and after the answer, above all that a saved AV1 choice
/// survives the wait. Each test hands the view model its own answer: the real one is process-wide, and any test that
/// builds the main view model starts the real probe, which overwrote it mid-test.
/// </summary>
public sealed class RecordingSettingsViewModelCodecTests
{
    private bool? _answer;

    private RecordingSettingsViewModel NewViewModel() => new(() => _answer);

    [Fact]
    public void BeforeTheProbeAnswers_TheListOffersH264AndH265()
    {
        var vm = NewViewModel();

        Assert.Equal(new[] { VideoCodec.H264, VideoCodec.H265 }, vm.VideoCodecOptions.Select(o => o.Value));
    }

    [Fact]
    public void SavedAv1_StaysSelected_WhileTheProbeHasNotAnswered()
    {
        var vm = NewViewModel();

        vm.SelectVideoCodec(VideoCodec.Av1);

        Assert.Contains(vm.VideoCodecOptions, o => o.Value == VideoCodec.Av1);
        Assert.Equal(VideoCodec.Av1, vm.SelectedVideoCodecOption?.Value);
        Assert.Equal(VideoCodec.Av1, vm.VideoCodec);
    }

    [Fact]
    public void SavedAv1_IsKept_WhenTheProbeConfirmsIt()
    {
        var vm = NewViewModel();
        vm.SelectVideoCodec(VideoCodec.Av1);

        _answer = true;
        vm.ApplyHardwareAv1ProbeResult();

        Assert.Contains(vm.VideoCodecOptions, o => o.Value == VideoCodec.Av1);
        Assert.Equal(VideoCodec.Av1, vm.VideoCodec);
    }

    [Fact]
    public void SavedAv1_FallsBackToTheFirstCodec_WhenTheProbeRulesItOut()
    {
        var vm = NewViewModel();
        vm.SelectVideoCodec(VideoCodec.Av1);

        _answer = false;
        vm.ApplyHardwareAv1ProbeResult();

        Assert.DoesNotContain(vm.VideoCodecOptions, o => o.Value == VideoCodec.Av1);
        Assert.Equal(VideoCodec.H264, vm.SelectedVideoCodecOption?.Value);
        Assert.Equal(VideoCodec.H264, vm.VideoCodec);
    }

    [Fact]
    public void ProbeAnsweringBeforeSettingsLoad_StillGivesTheRightSelection()
    {
        _answer = false;
        var vm = NewViewModel();
        vm.ApplyHardwareAv1ProbeResult();

        vm.SelectVideoCodec(VideoCodec.Av1);

        Assert.Equal(new[] { VideoCodec.H264, VideoCodec.H265 }, vm.VideoCodecOptions.Select(o => o.Value));
        Assert.Equal(VideoCodec.H264, vm.VideoCodec);
    }

    [Fact]
    public void OtherCodecs_AreUntouchedByTheProbe()
    {
        var vm = NewViewModel();
        vm.SelectVideoCodec(VideoCodec.H265);

        _answer = true;
        vm.ApplyHardwareAv1ProbeResult();

        Assert.Equal(VideoCodec.H265, vm.VideoCodec);
        Assert.Contains(vm.VideoCodecOptions, o => o.Value == VideoCodec.Av1);
    }
}
