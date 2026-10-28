using System;
using System.Collections.Concurrent;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit;

namespace GimmeCapture.Tests;

// The video player runs again for every seek, segment, loop pass, thumbnail and frame step of a pin or the editor, and
// each run logged its decode path, and every slow open, at Information. These decide what still reaches the log at
// Information; the repeats go to Debug.
public class LibavVideoFramePlayerLoggingTests
{
    private const string Hardware = "D3D11VA hardware decode";
    private const string NoGpuConfig = "software decode (codec has no D3D11VA config)";
    private const string DeviceFailed = "software decode (av_hwdevice_ctx_create failed)";

    [Fact]
    public void ADecodePath_IsReportedTheFirstTime_ButNotWhenItRepeats()
    {
        var last = new ConcurrentDictionary<AVCodecID, string>();

        Assert.True(LibavVideoFramePlayer.IsNewDecodePath(last, AVCodecID.AV_CODEC_ID_H264, Hardware));
        Assert.False(LibavVideoFramePlayer.IsNewDecodePath(last, AVCodecID.AV_CODEC_ID_H264, Hardware));
        Assert.False(LibavVideoFramePlayer.IsNewDecodePath(last, AVCodecID.AV_CODEC_ID_H264, Hardware));
    }

    [Fact]
    public void EachCodec_HasItsOwnDecodePath()
    {
        // A compare window plays an H.264 source and an AV1 sample side by side, window after window.
        var last = new ConcurrentDictionary<AVCodecID, string>();

        Assert.True(LibavVideoFramePlayer.IsNewDecodePath(last, AVCodecID.AV_CODEC_ID_H264, Hardware));
        Assert.True(LibavVideoFramePlayer.IsNewDecodePath(last, AVCodecID.AV_CODEC_ID_AV1, NoGpuConfig));
        Assert.False(LibavVideoFramePlayer.IsNewDecodePath(last, AVCodecID.AV_CODEC_ID_H264, Hardware));
        Assert.False(LibavVideoFramePlayer.IsNewDecodePath(last, AVCodecID.AV_CODEC_ID_AV1, NoGpuConfig));
    }

    [Fact]
    public void ADecodePathThatChanges_IsReportedEachTime()
    {
        var last = new ConcurrentDictionary<AVCodecID, string>();
        LibavVideoFramePlayer.IsNewDecodePath(last, AVCodecID.AV_CODEC_ID_HEVC, Hardware);

        Assert.True(LibavVideoFramePlayer.IsNewDecodePath(last, AVCodecID.AV_CODEC_ID_HEVC, DeviceFailed));
        Assert.True(LibavVideoFramePlayer.IsNewDecodePath(last, AVCodecID.AV_CODEC_ID_HEVC, Hardware));
    }

    [Fact]
    public void AFileSlowToOpen_IsReportedOnce()
    {
        var reported = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        Assert.True(LibavVideoFramePlayer.IsFirstSlowOpen(reported, @"C:\clips\long.mp4"));
        Assert.False(LibavVideoFramePlayer.IsFirstSlowOpen(reported, @"C:\clips\long.mp4"));
        Assert.True(LibavVideoFramePlayer.IsFirstSlowOpen(reported, @"C:\clips\other.mp4"));
    }

    [Fact]
    public void TheFilesRememberedAsSlow_StayBounded()
    {
        var reported = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < 1000; i++)
        {
            LibavVideoFramePlayer.IsFirstSlowOpen(reported, $"clip{i}.mp4");
        }

        Assert.InRange(reported.Count, 1, 256);
    }
}
