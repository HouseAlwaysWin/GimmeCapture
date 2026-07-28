using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// The recording encoder ladder: which encoders are tried, in what order, for each codec. Pure, so it is
/// assertable without libav — and it is where the AV1 policy lives, which is the part most likely to be
/// "helpfully" broken later by someone adding libsvtav1 to the list.
/// </summary>
public class LibavRecordingEncoderLadderTests
{
    private static readonly string[] SoftwareAv1Encoders = ["libsvtav1", "libaom-av1"];

    [Fact]
    public void Av1_PrefersHardwareEncodersFirst()
    {
        var ladder = LibavRecordingEncoder.BuildEncoderLadder(VideoCodec.Av1, preferHardware: true);

        Assert.Equal("av1_nvenc", ladder[0]);
        Assert.Contains("av1_qsv", ladder);
        Assert.Contains("av1_amf", ladder);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Av1_NeverFallsBackToASoftwareAv1Encoder(bool preferHardware)
    {
        // libsvtav1/libaom-av1 are offline encoders. They are in the build and Compress uses them, but during a
        // realtime capture they cannot keep up, so "AV1 unavailable" must degrade to H.265, not to dropped frames.
        var ladder = LibavRecordingEncoder.BuildEncoderLadder(VideoCodec.Av1, preferHardware);

        foreach (string softwareAv1 in SoftwareAv1Encoders)
        {
            Assert.DoesNotContain(softwareAv1, ladder);
        }
    }

    [Fact]
    public void Av1_FallsBackToTheH265Ladder()
    {
        var ladder = LibavRecordingEncoder.BuildEncoderLadder(VideoCodec.Av1, preferHardware: true);

        Assert.Contains("hevc_nvenc", ladder);
        Assert.Contains("libx265", ladder);
        // Every AV1 candidate is tried before any fallback, so a capable machine never silently gets H.265.
        int lastAv1 = ladder.AsSpan().LastIndexOf("av1_amf");
        int firstFallback = ladder.AsSpan().IndexOf("hevc_nvenc");
        Assert.True(lastAv1 < firstFallback, "AV1 candidates must all be tried before the H.265 fallback.");
    }

    [Fact]
    public void Av1_WithSoftwareOnlyRequested_UsesTheSoftwareH265Ladder()
    {
        // "Software only" plus AV1 is unsatisfiable in realtime; honour the software constraint, drop the codec.
        var ladder = LibavRecordingEncoder.BuildEncoderLadder(VideoCodec.Av1, preferHardware: false);

        Assert.Equal("libx265", ladder[0]);
        Assert.DoesNotContain(ladder, name => name.StartsWith("av1_", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(VideoCodec.H264, true, "h264_nvenc")]
    [InlineData(VideoCodec.H264, false, "libx264")]
    [InlineData(VideoCodec.H265, true, "hevc_nvenc")]
    [InlineData(VideoCodec.H265, false, "libx265")]
    public void ExistingCodecs_KeepTheirLadders(VideoCodec codec, bool preferHardware, string expectedFirst)
    {
        var ladder = LibavRecordingEncoder.BuildEncoderLadder(codec, preferHardware);

        Assert.Equal(expectedFirst, ladder[0]);
        Assert.DoesNotContain(ladder, name => name.StartsWith("av1", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(VideoCodec.Av1, "av1_nvenc", true)]
    [InlineData(VideoCodec.Av1, "hevc_nvenc", false)]
    [InlineData(VideoCodec.Av1, "libx265", false)]
    [InlineData(VideoCodec.H265, "hevc_qsv", true)]
    [InlineData(VideoCodec.H265, "libx265", true)]
    [InlineData(VideoCodec.H265, "libx264", false)]
    [InlineData(VideoCodec.H264, "libx264", true)]
    public void ProducesRequestedCodec_DecidesWhetherAFallbackWarningIsWarranted(
        VideoCodec codec,
        string encoderName,
        bool expected)
    {
        // Note hevc_nvenc + H265 is TRUE: the old check compared against the software anchor name, so every
        // hardware H.265 recording reported "libx265 unavailable, falling back to hevc_nvenc" — a fallback
        // warning for the encoder the user actually wanted.
        Assert.Equal(expected, LibavRecordingEncoder.ProducesRequestedCodec(codec, encoderName));
    }
}
