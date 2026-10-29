using FFmpeg.AutoGen;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// The recording encoder asks its software fallbacks for every core. Through the API libavcodec gives an encoder one
/// thread unless it is asked for more: libx264 defaults itself to auto and libx265 under zerolatency keeps one frame
/// thread whatever it is told, but the last-ditch mpeg4 really did encode on a single thread. Uses the real encoders,
/// so it needs the bundled FFmpeg, which every CI job provisions; runs everywhere.
/// </summary>
public class LibavRecordingEncoderTests
{
    [Theory]
    [InlineData("libx264")]
    [InlineData("libx265")]
    [InlineData("mpeg4")]
    public unsafe void ASoftwareEncoder_IsAskedForAutoThreads(string encoderName)
    {
        FFmpegRuntime.EnsureInitialized();
        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
        Assert.True(codec != null, $"{encoderName} is not in the bundled FFmpeg");
        AVCodecContext* encoder = ffmpeg.avcodec_alloc_context3(codec);
        AVDictionary* options = null;
        try
        {
            LibavRecordingEncoder.ConfigureEncoderContext(encoder, codec, encoderName, 640, 480, 30, 23, 0, &options);

            Assert.Equal(0, encoder->thread_count);
        }
        finally
        {
            Free(encoder, options);
        }
    }

    [Theory]
    [InlineData("h264_nvenc", true)]
    [InlineData("hevc_nvenc", true)]
    [InlineData("av1_nvenc", true)]
    [InlineData("h264_qsv", false)]
    [InlineData("hevc_amf", false)]
    [InlineData("h264_mf", false)]
    [InlineData("libx264", false)]
    public void TheAv1Probe_TriesOnlyNvencOnDirect3D11FramesFirst_OnWindows(string encoderName, bool isNvenc)
    {
        // Given frames in memory, NVENC loads CUDA, whose driver keeps 190 MB of the process for good.
        bool[] expected = isNvenc && OperatingSystem.IsWindows() ? [true, false] : [false];

        Assert.Equal(expected, LibavRecordingEncoder.ProbeFrameAttempts(encoderName));
    }

    /// <summary>
    /// The AV1 probe the Record tab runs leaves little of the process behind. On CUDA it left 190 MB for good, recording
    /// or not. Opt-in: it needs an NVIDIA GPU with NVENC, and reads the private bytes of the whole test process, so it
    /// is best run on its own.
    /// </summary>
    [OptInFact("NVENC_PROBE=1")]
    public void TheAv1Probe_LeavesLittleOfTheProcessBehind()
    {
        FFmpegRuntime.EnsureInitialized();
        long baseline = PrivateBytes();

        LibavRecordingEncoder.ProbeHardwareAv1();

        Assert.InRange(PrivateBytes() - baseline, long.MinValue, 80L * 1024 * 1024);
    }

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    [Fact]
    public void WarmUpEncoder_OpensTheEncoderARecordingWouldGet()
    {
        FFmpegRuntime.EnsureInitialized();

        Assert.Equal("libx264", LibavRecordingEncoder.WarmUpEncoder(VideoCodec.H264, preferHardware: false));
        Assert.Equal("libx265", LibavRecordingEncoder.WarmUpEncoder(VideoCodec.H265, preferHardware: false));
    }

    [Fact]
    public unsafe void Libx264_KeepsZerolatencysSlicedThreads()
    {
        // libx264 turns zerolatency's sliced threads into frame threads, which hold frames back, whenever thread_type
        // is set; its own default leaves it unset, and the recording encoder must not set it.
        FFmpegRuntime.EnsureInitialized();
        AVDictionary* options = null;
        AVCodecContext* encoder = LibavRecordingEncoder.OpenRecordingEncoderContext(
            VideoCodec.H264, preferHardware: false, width: 640, height: 480, fps: 30, crf: 23, bitrateOverride: 0, &options,
            out string name, out _);
        try
        {
            Assert.Equal("libx264", name);
            Assert.Equal(0, encoder->thread_count);
            Assert.Equal(0, encoder->thread_type);
        }
        finally
        {
            Free(encoder, options);
        }
    }

    private static unsafe void Free(AVCodecContext* encoder, AVDictionary* options)
    {
        if (options != null)
        {
            ffmpeg.av_dict_free(&options);
        }

        ffmpeg.avcodec_free_context(&encoder);
    }
}
