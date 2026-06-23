using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

public class RecordingFormatMappingTests
{
    [Theory]
    [InlineData("mp4", "mp4")]
    [InlineData("mov", "mov")]
    [InlineData("webm", "webm")]
    [InlineData("mkv", "mkv")]
    [InlineData("gif", "mkv")]      // unknown-to-switch -> default mkv
    [InlineData("MP4", "mkv")]      // mapping is case-sensitive
    public void GetTargetExtension_MapsFormatsWithMkvDefault(string format, string expected)
    {
        Assert.Equal(expected, RecordingService.GetTargetExtension(format));
    }

    [Theory]
    [InlineData("mp4", "mp4")]
    [InlineData("mov", "mov")]
    [InlineData("webm", "webm")]
    [InlineData("mkv", "matroska")]
    [InlineData("gif", "matroska")] // unknown-to-switch -> default matroska
    public void GetMuxerFormatName_MapsFFmpegContainerWithMatroskaDefault(string format, string expected)
    {
        Assert.Equal(expected, RecordingService.GetMuxerFormatName(format));
    }

    [Theory]
    [InlineData("h264_nvenc", true)]
    [InlineData("hevc_mf", true)]
    [InlineData("h264_qsv", true)]
    [InlineData("h264_amf", true)]
    [InlineData("h264_vaapi", true)]
    [InlineData("hevc_d3d12va", true)]
    [InlineData("libx264", false)]
    [InlineData("libx265", false)]
    [InlineData("", false)]
    public void RequiresMatroskaFinalization_DetectsHardwareEncoders(string encoderName, bool expected)
    {
        Assert.Equal(expected, RecordingService.RequiresMatroskaFinalization(encoderName));
    }
}
