using System;
using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Shared libav encoder/muxer helpers for the recording sessions. Both the gdigrab desktop-region
/// session (<see cref="LibavGdigrabMkvSession"/>) and the WGC per-window session
/// (<see cref="LibavWgcMkvSession"/>) open the same encoder ladder (hardware → software fallback) and
/// drain encoded packets the same way, so that logic lives here once.
/// </summary>
internal static unsafe class LibavRecordingEncoder
{
    /// <summary>
    /// Opens the best available video encoder for recording. Hardware / Media-Foundation encoders are
    /// tried first (when <paramref name="preferHardware"/> is set) so the recording is GPU-accelerated
    /// where available; each is test-opened and we fall back to the next, ending with the always-available
    /// software libx264/265. Returns the opened context, or throws if nothing works.
    /// </summary>
    public static AVCodecContext* OpenRecordingEncoderContext(
        bool preferH265,
        bool preferHardware,
        int width,
        int height,
        int fps,
        int crf,
        long bitrateOverride,
        AVDictionary** encOpts,
        out string selectedEncoderName,
        out string? warningMessage)
    {
        string[] hwH265 = ["hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_mf"];
        string[] hwH264 = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf"];
        string[] swH265 = ["libx265", "libx264", "libopenh264", "mpeg4"];
        string[] swH264 = ["libx264", "libopenh264", "mpeg4"];

        string[] preferredNames = (preferH265, preferHardware) switch
        {
            (true, true) => [.. hwH265, .. swH265],
            (true, false) => swH265,
            (false, true) => [.. hwH264, .. swH264],
            (false, false) => swH264,
        };

        selectedEncoderName = preferH265 ? "libx265" : "libx264";
        warningMessage = null;
        string requestedEncoderName = selectedEncoderName;
        string? lastOpenError = null;
        bool foundRequestedEncoder = false;

        foreach (string candidateName in preferredNames)
        {
            AVCodec* candidate = ffmpeg.avcodec_find_encoder_by_name(candidateName);
            if (candidate == null)
            {
                LogNative($"Encoder candidate unavailable: {candidateName}");
                continue;
            }

            if (candidateName == requestedEncoderName)
            {
                foundRequestedEncoder = true;
            }

            AVCodecContext* candidateCtx = ffmpeg.avcodec_alloc_context3(candidate);
            if (candidateCtx == null)
            {
                LogNative($"Encoder candidate alloc failed: {candidateName}");
                continue;
            }

            AVDictionary* candidateOpts = null;
            try
            {
                ConfigureEncoderContext(candidateCtx, candidate, candidateName, width, height, fps, crf, bitrateOverride, &candidateOpts);
                int openResult = ffmpeg.avcodec_open2(candidateCtx, candidate, &candidateOpts);
                if (openResult >= 0)
                {
                    AVCodecContext* openedCtx = candidateCtx;
                    if (encOpts != null)
                    {
                        *encOpts = candidateOpts;
                    }
                    candidateCtx = null;
                    candidateOpts = null;

                    selectedEncoderName = candidateName;
                    if (preferH265 && candidateName != requestedEncoderName)
                    {
                        warningMessage = $"Encoder '{requestedEncoderName}' unavailable. Falling back to '{candidateName}'.";
                    }

                    return openedCtx;
                }

                lastOpenError = $"{candidateName}: {FFmpegErrors.Describe(openResult)} ({openResult})";
                LogNative($"Encoder candidate open failed: {lastOpenError}");
            }
            finally
            {
                if (candidateOpts != null)
                {
                    ffmpeg.av_dict_free(&candidateOpts);
                }

                if (candidateCtx != null)
                {
                    ffmpeg.avcodec_free_context(&candidateCtx);
                }
            }
        }

        if (!foundRequestedEncoder)
        {
            throw new InvalidOperationException($"Encoder '{requestedEncoderName}' unavailable.");
        }

        throw new InvalidOperationException($"No compatible video encoder available. Last error: {lastOpenError ?? "unknown"}");
    }

    private static void ConfigureEncoderContext(
        AVCodecContext* encCtx,
        AVCodec* enc,
        string encoderName,
        int width,
        int height,
        int fps,
        int crf,
        long bitrateOverride,
        AVDictionary** encOpts)
    {
        encCtx->codec_id = enc->id;
        encCtx->width = width;
        encCtx->height = height;
        encCtx->pix_fmt = ChooseEncoderPixelFormat(enc, encoderName);
        encCtx->time_base = new AVRational { num = 1, den = fps };
        encCtx->framerate = new AVRational { num = fps, den = 1 };
        encCtx->gop_size = fps * 2;
        encCtx->max_b_frames = 0;
        encCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (string.Equals(encoderName, "libx264", StringComparison.OrdinalIgnoreCase)
            || string.Equals(encoderName, "libx265", StringComparison.OrdinalIgnoreCase))
        {
            ffmpeg.av_dict_set(encOpts, "preset", "ultrafast", 0);
            ffmpeg.av_dict_set(encOpts, "tune", "zerolatency", 0);
            // Software path is CRF-driven. Honor a user CRF override (1-51); 0 keeps the default 23.
            int effectiveCrf = crf is >= 1 and <= 51 ? crf : 23;
            ffmpeg.av_dict_set(encOpts, "crf", effectiveCrf.ToString(), 0);
            return;
        }

        // Hardware path is bitrate-driven. Honor a user bitrate override; 0 uses the automatic clamp.
        long targetBitrate = bitrateOverride > 0
            ? bitrateOverride
            : Math.Clamp((long)width * height * Math.Max(fps, 1) / 8, 800_000L, 16_000_000L);
        encCtx->bit_rate = targetBitrate;
    }

    private static AVPixelFormat ChooseEncoderPixelFormat(AVCodec* enc, string encoderName)
    {
        AVPixelFormat[] preferredFormats = IsHardwareOrMediaFoundationEncoder(encoderName)
            ? [AVPixelFormat.AV_PIX_FMT_NV12, AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_P010LE]
            : [AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_NV12];

        if (enc->pix_fmts == null)
        {
            return preferredFormats[0];
        }

        for (int i = 0; enc->pix_fmts[i] != AVPixelFormat.AV_PIX_FMT_NONE; i++)
        {
            foreach (var preferredFormat in preferredFormats)
            {
                if (enc->pix_fmts[i] == preferredFormat)
                {
                    return preferredFormat;
                }
            }
        }

        return enc->pix_fmts[0];
    }

    private static bool IsHardwareOrMediaFoundationEncoder(string encoderName)
    {
        return encoderName.Contains("_mf", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_nvenc", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_qsv", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_amf", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_vaapi", StringComparison.OrdinalIgnoreCase)
            || encoderName.Contains("_d3d12va", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drains all ready encoded packets from <paramref name="encCtx"/> into the muxer.</summary>
    public static void WriteEncodedPackets(
        AVCodecContext* encCtx,
        AVFormatContext* outputFmt,
        AVStream* outStream,
        AVPacket* pkt,
        ref long packetCounter)
    {
        while (true)
        {
            int gp = ffmpeg.avcodec_receive_packet(encCtx, pkt);
            if (gp == ffmpeg.AVERROR_EOF || gp == ffmpeg.AVERROR(11))
            {
                break;
            }
            ThrowIfErr(gp, "receive_packet(enc)");

            ffmpeg.av_packet_rescale_ts(pkt, encCtx->time_base, outStream->time_base);
            pkt->stream_index = outStream->index;
            ThrowIfErr(ffmpeg.av_interleaved_write_frame(outputFmt, pkt), "write_packet(enc)");
            packetCounter++;
            ffmpeg.av_packet_unref(pkt);
        }
    }

    public static void ThrowIfErr(int err, string ctx)
    {
        if (err < 0)
        {
            throw FFmpegErrors.ToException(err, ctx);
        }
    }

    private static void LogNative(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[RecordingNative] {message}");
    }
}
