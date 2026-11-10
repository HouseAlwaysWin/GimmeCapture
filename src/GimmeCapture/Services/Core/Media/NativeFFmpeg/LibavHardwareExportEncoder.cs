using System;
using FFmpeg.AutoGen;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// NVIDIA's hardware encoders for an export. Opened with the settings an offline encode can afford, unlike a recording's:
/// B-frames, look-ahead and adaptive quantization, at the quality or the bitrate asked for. Null from
/// <see cref="TryOpen"/> means this machine cannot do it, and the export stops for its caller to decide.
/// </summary>
internal static unsafe class LibavHardwareExportEncoder
{
    /// <param name="BFrames">How many B-frames to allow.</param>
    /// <param name="BFramesAsReferences">Whether B-frames may be used as references (Turing and later).</param>
    internal readonly record struct Attempt(int BFrames, bool BFramesAsReferences);

    // From what the newest GPUs do down to what every NVENC does: FFmpeg refuses an encoder asked for more than the GPU has.
    private static readonly Attempt[] Attempts = [new(3, true), new(3, false), new(0, false)];

    private static int _loggedOpen;

    internal static string EncoderName(VideoCodec codec) => codec switch
    {
        VideoCodec.Av1 => "av1_nvenc",
        VideoCodec.H265 => "hevc_nvenc",
        _ => "h264_nvenc",
    };

    /// <summary>The window's speed preset as NVENC's p1 (fastest) to p7 (slowest). Pure.</summary>
    internal static string PresetFor(string? preset) => preset switch
    {
        "ultrafast" => "p1",
        "superfast" or "veryfast" => "p2",
        "faster" => "p3",
        "fast" => "p4",
        "medium" => "p5",
        "slow" => "p6",
        "slower" or "veryslow" or "placebo" => "p7",
        _ => "p4",
    };

    /// <summary>The pixels NVENC is handed: AV1 in 10 bits, as the software AV1 path encodes it, the rest in 8.</summary>
    internal static AVPixelFormat PixelFormatFor(VideoCodec codec) =>
        codec == VideoCodec.Av1 ? AVPixelFormat.AV_PIX_FMT_P010LE : AVPixelFormat.AV_PIX_FMT_YUV420P;

    /// <summary>
    /// Opens the encoder for <paramref name="codec"/>, at the constant quality <paramref name="quality"/>, or at
    /// <paramref name="targetBitrateKbps"/> when that is above zero. Null when no attempt opens.
    /// </summary>
    internal static AVCodecContext* TryOpen(
        VideoCodec codec, int width, int height, AVRational rate, bool globalHeader, int quality, int targetBitrateKbps, string? preset)
    {
        string name = EncoderName(codec);
        AVCodec* encoder = ffmpeg.avcodec_find_encoder_by_name(name);
        if (encoder == null)
        {
            AppLog.Warning("Export.HardwareEncoder", $"{name} is not in this build of FFmpeg.");
            return null;
        }

        DisplayAdapterOrder.SettleBeforeCuda();
        int lastError = 0;
        foreach (Attempt attempt in Attempts)
        {
            AVCodecContext* context = ffmpeg.avcodec_alloc_context3(encoder);
            if (context == null)
            {
                return null;
            }

            AVDictionary* options = null;
            try
            {
                Configure(context, encoder, codec, width, height, rate, globalHeader, quality, targetBitrateKbps, preset, attempt, &options);
                lastError = ffmpeg.avcodec_open2(context, encoder, &options);
                if (lastError >= 0)
                {
                    LogFirstOpen(codec, name, attempt, preset);
                    AVCodecContext* opened = context;
                    context = null;
                    return opened;
                }
            }
            finally
            {
                if (options != null)
                {
                    ffmpeg.av_dict_free(&options);
                }

                if (context != null)
                {
                    ffmpeg.avcodec_free_context(&context);
                }
            }
        }

        AppLog.Warning(
            "Export.HardwareEncoder", $"{name} did not open at {width}x{height}: {FFmpegErrors.Describe(lastError)} ({lastError})");
        return null;
    }

    // Once a run for each codec: a compress opens one for every 30 s of video.
    private static void LogFirstOpen(VideoCodec codec, string name, Attempt attempt, string? preset)
    {
        int bit = 1 << (int)codec;
        if ((System.Threading.Interlocked.Or(ref _loggedOpen, bit) & bit) == 0)
        {
            AppLog.Information(
                "Export.HardwareEncoder",
                $"{name} {PresetFor(preset)}: {attempt.BFrames} B-frames, as references {attempt.BFramesAsReferences}");
        }
    }

    private static void Configure(
        AVCodecContext* context, AVCodec* encoder, VideoCodec codec, int width, int height, AVRational rate, bool globalHeader,
        int quality, int targetBitrateKbps, string? preset, Attempt attempt, AVDictionary** options)
    {
        context->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        context->codec_id = encoder->id;
        context->width = width;
        context->height = height;
        context->pix_fmt = PixelFormatFor(codec);
        context->time_base = new AVRational { num = rate.den, den = rate.num };
        context->framerate = rate;
        context->gop_size = (int)Math.Round(2.0 * rate.num / rate.den);
        context->max_b_frames = attempt.BFrames;
        if (globalHeader)
        {
            context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        }

        ffmpeg.av_dict_set(options, "preset", PresetFor(preset), 0);
        ffmpeg.av_dict_set(options, "tune", "hq", 0);
        ffmpeg.av_dict_set(options, "rc", "vbr", 0);
        ffmpeg.av_dict_set(options, "spatial-aq", "1", 0);
        ffmpeg.av_dict_set(options, "rc-lookahead", "20", 0);
        if (attempt.BFramesAsReferences)
        {
            ffmpeg.av_dict_set(options, "b_ref_mode", "middle", 0);
        }

        if (targetBitrateKbps > 0)
        {
            long bitsPerSecond = targetBitrateKbps * 1000L;
            context->bit_rate = bitsPerSecond;
            context->rc_max_rate = bitsPerSecond * 2;
            context->rc_buffer_size = (int)Math.Min(int.MaxValue, bitsPerSecond * 2);
            ffmpeg.av_dict_set(options, "multipass", "fullres", 0);
        }
        else
        {
            // The context's default bitrate would otherwise cap a constant-quality encode.
            context->bit_rate = 0;
            ffmpeg.av_dict_set(options, "cq", quality.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
            ffmpeg.av_dict_set(options, "multipass", "qres", 0);
        }
    }
}
