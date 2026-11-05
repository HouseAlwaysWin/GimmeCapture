using FFmpeg.AutoGen;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// The recording encoder asks its software fallbacks for every core. Through the API libavcodec gives an encoder one
/// thread unless it is asked for more: libx264 defaults itself to auto and libx265 under zerolatency keeps one frame
/// thread whatever it is told, but the last-ditch mpeg4 really did encode on a single thread. It also hands NVENC the
/// capture's BGRA to convert on the GPU. Uses the real encoders, so it needs the bundled FFmpeg, which every CI job
/// provisions; runs everywhere but where a test says otherwise.
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

    [Theory]
    [InlineData(VideoCodec.H264, true, true)]
    [InlineData(VideoCodec.H265, true, true)]
    [InlineData(VideoCodec.Av1, true, true)]
    [InlineData(VideoCodec.H264, false, false)]
    [InlineData(VideoCodec.H265, false, false)]
    [InlineData(VideoCodec.Av1, false, false)]
    public void TheRecordingWarmUp_StartsCudaInsteadOfOpeningNvenc(VideoCodec codec, bool preferHardware, bool expected)
    {
        // Every hardware ladder starts with NVENC, whose CUDA context the driver would keep 190 MB of for good.
        Assert.Equal(expected, LibavRecordingEncoder.WarmsUpThroughCuda(codec, preferHardware));
    }

    /// <summary>
    /// Entering Record mode leaves little of the process behind. Opening NVENC there kept 190 MB for good, recording or
    /// not; starting the CUDA driver alone keeps about 8. Opt-in like the AV1 probe above, and for the same reasons.
    /// </summary>
    [OptInFact("NVENC_PROBE=1")]
    public void TheRecordingWarmUp_LeavesLittleOfTheProcessBehind()
    {
        FFmpegRuntime.EnsureInitialized();
        long baseline = PrivateBytes();

        string warmedUp = LibavRecordingEncoder.WarmUpEncoder(VideoCodec.H264, preferHardware: true);

        Assert.Equal("cuInit", warmedUp);
        Assert.InRange(PrivateBytes() - baseline, long.MinValue, 40L * 1024 * 1024);
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

    // Every recording source hands over BGRA, which NVENC converts on the GPU to the colours the CPU's conversion gave.
    // It was handed yuv420p, the first 4:2:0 it lists, converted on the recording thread: 1130-1260 M cycles a second of
    // a 1450x900 recording at 30 fps against 700-890 (RTX 3050 Laptop). The other encoders keep what they took.
    [Fact]
    public void Nvenc_IsHandedTheCapturesBgra_OtherEncodersWhatTheyTookBefore()
    {
        // hevc_nvenc's list in the bundled FFmpeg, in its order (h264_nvenc and av1_nvenc list BGRA too).
        AVPixelFormat[] nvenc =
        [
            AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_NV12, AVPixelFormat.AV_PIX_FMT_P010LE,
            AVPixelFormat.AV_PIX_FMT_YUV444P, AVPixelFormat.AV_PIX_FMT_BGR0, AVPixelFormat.AV_PIX_FMT_BGRA,
            AVPixelFormat.AV_PIX_FMT_RGB0, AVPixelFormat.AV_PIX_FMT_RGBA, AVPixelFormat.AV_PIX_FMT_CUDA,
            AVPixelFormat.AV_PIX_FMT_D3D11,
        ];

        Assert.Equal(AVPixelFormat.AV_PIX_FMT_BGRA, LibavRecordingEncoder.ChooseEncoderPixelFormat(nvenc, "hevc_nvenc"));
        Assert.Equal(AVPixelFormat.AV_PIX_FMT_BGRA, LibavRecordingEncoder.ChooseEncoderPixelFormat(nvenc, "h264_nvenc"));
        Assert.Equal(AVPixelFormat.AV_PIX_FMT_BGRA, LibavRecordingEncoder.ChooseEncoderPixelFormat(nvenc, "av1_nvenc"));

        // Opened again on 4:2:0 when its driver refuses BGRA, and on a build that does not list it: what it took before.
        Assert.Equal(
            AVPixelFormat.AV_PIX_FMT_YUV420P, LibavRecordingEncoder.ChooseEncoderPixelFormat(nvenc, "hevc_nvenc", allowBgra: false));
        Assert.Equal(
            AVPixelFormat.AV_PIX_FMT_YUV420P,
            LibavRecordingEncoder.ChooseEncoderPixelFormat([AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_NV12], "hevc_nvenc"));

        // AMD's AMF takes BGRA as well, and converts it with the BT.709 it writes into the stream whatever it is told.
        Assert.Equal(
            AVPixelFormat.AV_PIX_FMT_BGRA,
            LibavRecordingEncoder.ChooseEncoderPixelFormat(
                [AVPixelFormat.AV_PIX_FMT_NV12, AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_BGRA], "hevc_amf"));
        Assert.Equal(
            AVPixelFormat.AV_PIX_FMT_NV12,
            LibavRecordingEncoder.ChooseEncoderPixelFormat(
                [AVPixelFormat.AV_PIX_FMT_NV12, AVPixelFormat.AV_PIX_FMT_BGRA], "hevc_qsv"));
        Assert.Equal(
            AVPixelFormat.AV_PIX_FMT_YUV420P,
            LibavRecordingEncoder.ChooseEncoderPixelFormat(
                [AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_YUVJ420P, AVPixelFormat.AV_PIX_FMT_NV12], "libx265"));
        Assert.Equal(AVPixelFormat.AV_PIX_FMT_NV12, LibavRecordingEncoder.ChooseEncoderPixelFormat([], "hevc_qsv"));
    }

    [Fact]
    public void NvencAndAmf_AreOpenedOnBgraFirst_ThenOn420()
    {
        Assert.Equal(new[] { true, false }, LibavRecordingEncoder.BgraAttempts("hevc_nvenc"));
        Assert.Equal(new[] { true, false }, LibavRecordingEncoder.BgraAttempts("h264_nvenc"));
        Assert.Equal(new[] { true, false }, LibavRecordingEncoder.BgraAttempts("hevc_amf"));
        Assert.Equal(new[] { false }, LibavRecordingEncoder.BgraAttempts("hevc_qsv"));
        Assert.Equal(new[] { false }, LibavRecordingEncoder.BgraAttempts("libx265"));
    }

    // A recording says which colours it holds: left unsaid, players took BT.709 for HD and shifted the CPU's BT.601.
    [Theory]
    [InlineData("libx264", AVColorSpace.AVCOL_SPC_BT470BG)]
    [InlineData("libx265", AVColorSpace.AVCOL_SPC_BT470BG)]
    [InlineData("mpeg4", AVColorSpace.AVCOL_SPC_BT470BG)]
    public unsafe void ARecording_SaysTheColoursTheConverterWrites(string encoderName, AVColorSpace expected)
    {
        FFmpegRuntime.EnsureInitialized();
        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
        Assert.True(codec != null, $"{encoderName} is not in the bundled FFmpeg");
        AVCodecContext* encoder = ffmpeg.avcodec_alloc_context3(codec);
        AVDictionary* options = null;
        try
        {
            LibavRecordingEncoder.ConfigureEncoderContext(encoder, codec, encoderName, 640, 480, 30, 23, 0, &options);

            Assert.Equal(expected, encoder->colorspace);
            Assert.Equal(AVColorRange.AVCOL_RANGE_MPEG, encoder->color_range);
        }
        finally
        {
            Free(encoder, options);
        }
    }

    // AMD's encoder writes BT.709 into the stream whatever it is told, so it is handed BGRA, which it converts with BT.709,
    // and the context says so too. Its own 4:2:0, should it refuse BGRA, is written as BT.709 by the converters.
    [WindowsFact]
    public unsafe void HevcAmf_IsConfiguredForBgra_AsTheBt709ItWrites()
    {
        FFmpegRuntime.EnsureInitialized();
        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name("hevc_amf");
        Assert.True(codec != null, "hevc_amf is not in the bundled FFmpeg");
        foreach (bool allowBgra in new[] { true, false })
        {
            AVCodecContext* encoder = ffmpeg.avcodec_alloc_context3(codec);
            AVDictionary* options = null;
            try
            {
                LibavRecordingEncoder.ConfigureEncoderContext(encoder, codec, "hevc_amf", 1450, 900, 30, 0, 0, &options, allowBgra);

                Assert.Equal(allowBgra ? AVPixelFormat.AV_PIX_FMT_BGRA : AVPixelFormat.AV_PIX_FMT_NV12, encoder->pix_fmt);
                Assert.Equal(AVColorSpace.AVCOL_SPC_BT709, encoder->colorspace);
            }
            finally
            {
                Free(encoder, options);
            }
        }
    }

    // The bundled hevc_nvenc itself, configured as a recording configures it: nothing is opened, so no GPU is needed.
    // Windows only because only the Windows FFmpeg bundle is known to carry NVENC.
    [WindowsFact]
    public unsafe void HevcNvenc_IsConfiguredForBgra_WithTheColoursItWillWrite()
    {
        FFmpegRuntime.EnsureInitialized();
        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name("hevc_nvenc");
        Assert.True(codec != null, "hevc_nvenc is not in the bundled FFmpeg");
        AVCodecContext* encoder = ffmpeg.avcodec_alloc_context3(codec);
        AVDictionary* options = null;
        try
        {
            LibavRecordingEncoder.ConfigureEncoderContext(encoder, codec, "hevc_nvenc", 1450, 900, 30, 0, 0, &options);

            Assert.Equal(AVPixelFormat.AV_PIX_FMT_BGRA, encoder->pix_fmt);
            Assert.Equal(AVColorSpace.AVCOL_SPC_BT470BG, encoder->colorspace);
            Assert.Equal(AVColorRange.AVCOL_RANGE_MPEG, encoder->color_range);
        }
        finally
        {
            Free(encoder, options);
        }
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
