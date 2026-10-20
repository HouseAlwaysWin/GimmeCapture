using System;
using System.Linq;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// The codec list used to run the hardware AV1 probe — loading FFmpeg first — the moment settings touched it, on the
/// UI thread at startup (1.75 s measured). The probe now runs on the pool and reports back; these pin down what the
/// list does before and after it answers, above all that a saved AV1 choice survives the wait. The probe's cached
/// answer is process-wide, so every test sets it and puts it back.
/// </summary>
public sealed class RecordingSettingsViewModelCodecTests : IDisposable
{
    private readonly bool? _originalAnswer = LibavRecordingEncoder.KnownHardwareAv1Usable;

    public void Dispose() => LibavRecordingEncoder.SetKnownHardwareAv1UsableForTest(_originalAnswer);

    [Fact]
    public void ReadingTheCodecList_DoesNotRunTheProbe()
    {
        LibavRecordingEncoder.SetKnownHardwareAv1UsableForTest(null);
        var vm = new RecordingSettingsViewModel();

        var options = vm.VideoCodecOptions;

        Assert.Null(LibavRecordingEncoder.KnownHardwareAv1Usable);
        Assert.Equal(new[] { VideoCodec.H264, VideoCodec.H265 }, options.Select(o => o.Value));
    }

    [Fact]
    public void SavedAv1_StaysSelected_WhileTheProbeHasNotAnswered()
    {
        LibavRecordingEncoder.SetKnownHardwareAv1UsableForTest(null);
        var vm = new RecordingSettingsViewModel();

        vm.SelectVideoCodec(VideoCodec.Av1);

        Assert.Contains(vm.VideoCodecOptions, o => o.Value == VideoCodec.Av1);
        Assert.Equal(VideoCodec.Av1, vm.SelectedVideoCodecOption?.Value);
        Assert.Equal(VideoCodec.Av1, vm.VideoCodec);
    }

    [Fact]
    public void SavedAv1_IsKept_WhenTheProbeConfirmsIt()
    {
        LibavRecordingEncoder.SetKnownHardwareAv1UsableForTest(null);
        var vm = new RecordingSettingsViewModel();
        vm.SelectVideoCodec(VideoCodec.Av1);

        LibavRecordingEncoder.SetKnownHardwareAv1UsableForTest(true);
        vm.ApplyHardwareAv1ProbeResult();

        Assert.Contains(vm.VideoCodecOptions, o => o.Value == VideoCodec.Av1);
        Assert.Equal(VideoCodec.Av1, vm.VideoCodec);
    }

    [Fact]
    public void SavedAv1_FallsBackToTheFirstCodec_WhenTheProbeRulesItOut()
    {
        LibavRecordingEncoder.SetKnownHardwareAv1UsableForTest(null);
        var vm = new RecordingSettingsViewModel();
        vm.SelectVideoCodec(VideoCodec.Av1);

        LibavRecordingEncoder.SetKnownHardwareAv1UsableForTest(false);
        vm.ApplyHardwareAv1ProbeResult();

        Assert.DoesNotContain(vm.VideoCodecOptions, o => o.Value == VideoCodec.Av1);
        Assert.Equal(VideoCodec.H264, vm.SelectedVideoCodecOption?.Value);
        Assert.Equal(VideoCodec.H264, vm.VideoCodec);
    }

    [Fact]
    public void ProbeAnsweringBeforeSettingsLoad_StillGivesTheRightSelection()
    {
        LibavRecordingEncoder.SetKnownHardwareAv1UsableForTest(false);
        var vm = new RecordingSettingsViewModel();
        vm.ApplyHardwareAv1ProbeResult();

        vm.SelectVideoCodec(VideoCodec.Av1);

        Assert.Equal(new[] { VideoCodec.H264, VideoCodec.H265 }, vm.VideoCodecOptions.Select(o => o.Value));
        Assert.Equal(VideoCodec.H264, vm.VideoCodec);
    }

    [Fact]
    public void OtherCodecs_AreUntouchedByTheProbe()
    {
        LibavRecordingEncoder.SetKnownHardwareAv1UsableForTest(null);
        var vm = new RecordingSettingsViewModel();
        vm.SelectVideoCodec(VideoCodec.H265);

        LibavRecordingEncoder.SetKnownHardwareAv1UsableForTest(true);
        vm.ApplyHardwareAv1ProbeResult();

        Assert.Equal(VideoCodec.H265, vm.VideoCodec);
        Assert.Contains(vm.VideoCodecOptions, o => o.Value == VideoCodec.Av1);
    }
}
