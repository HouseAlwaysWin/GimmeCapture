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
