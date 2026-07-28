using System;
using FFmpeg.AutoGen;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Shared libav encoder/muxer helpers for the recording sessions. Both the gdigrab desktop-region
/// session (<see cref="LibavGdigrabMkvSession"/>) and the WGC per-window session
/// (<see cref="LibavWgcMkvSession"/>) open the same encoder ladder (hardware → software fallback) and
/// drain encoded packets the same way, so that logic lives here once.
/// </summary>
internal static unsafe class LibavRecordingEncoder
{
    // Realtime-capable AV1 encoders only. libsvtav1/libaom-av1 are deliberately absent — see the ladder below.
    private static readonly string[] HardwareAv1Encoders = ["av1_nvenc", "av1_qsv", "av1_amf"];
    private static readonly string[] HardwareH265Encoders = ["hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_mf"];
    private static readonly string[] HardwareH264Encoders = ["h264_nvenc", "h264_qsv", "h264_amf", "h264_mf"];
    private static readonly string[] SoftwareH265Encoders = ["libx265", "libx264", "libopenh264", "mpeg4"];
    private static readonly string[] SoftwareH264Encoders = ["libx264", "libopenh264", "mpeg4"];

    // 0 = not probed yet, 1 = usable, 2 = not usable. Only ever set from a probe that actually ran, so a probe
    // attempted before the native libraries were loaded does not poison the answer for the rest of the session.
    private static int _hardwareAv1State;

    /// <summary>
    /// Whether this machine can really encode AV1 in hardware.
    ///
    /// This has to TEST-OPEN an encoder. <c>avcodec_find_encoder_by_name("av1_nvenc")</c> only proves the encoder
    /// was compiled into the build — it says nothing about the GPU, and every vendor ships an AV1 encoder that
    /// exists but fails at open time on hardware that cannot do it (NVIDIA before Ada / RTX 40, AMD before RDNA3,
    /// Intel before Arc). Offering AV1 on the strength of the lookup alone would put an option in the UI that
    /// silently degrades to H.265 on most machines.
    ///
    /// Cached for the process: opening an NVENC context is not free.
    /// </summary>
    public static bool HasUsableHardwareAv1Encoder()
    {
        int cached = System.Threading.Volatile.Read(ref _hardwareAv1State);
        if (cached != 0)
        {
            return cached == 1;
        }

        bool usable;
        try
        {
            if (!FFmpegRuntime.TryInitialize(out _))
            {
                // Cannot answer yet, and must not remember this as "no".
                return false;
            }

            usable = ProbeHardwareAv1();
        }
        catch (Exception ex)
        {
            AppLog.Warning("Recording.Av1Probe", ex);
            return false;
        }

        System.Threading.Volatile.Write(ref _hardwareAv1State, usable ? 1 : 2);
        AppLog.Information($"Recording.HardwareAv1Probe: {(usable ? "available" : "unavailable")}");
        return usable;
    }

    private static bool ProbeHardwareAv1()
    {
        // Small but real: some encoders reject tiny or odd dimensions, so probe at a size a recording could use.
        const int ProbeWidth = 640;
        const int ProbeHeight = 480;
        const int ProbeFps = 30;
        const int ProbeCrf = 23;

        foreach (string candidateName in HardwareAv1Encoders)
        {
            AVCodec* candidate = ffmpeg.avcodec_find_encoder_by_name(candidateName);
            if (candidate == null)
            {
                continue;
            }

            AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(candidate);
            if (ctx == null)
            {
                continue;
            }

            AVDictionary* opts = null;
            try
            {
                ConfigureEncoderContext(ctx, candidate, candidateName, ProbeWidth, ProbeHeight, ProbeFps, ProbeCrf, 0, &opts);
                if (ffmpeg.avcodec_open2(ctx, candidate, &opts) >= 0)
                {
                    LogNative($"Hardware AV1 encoder usable: {candidateName}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLog.Warning("Recording.Av1Probe", ex);
            }
            finally
            {
                if (opts != null) ffmpeg.av_dict_free(&opts);
                if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
            }
        }

        return false;
    }

    /// <summary>
    /// The ordered encoder candidates for a codec, each test-opened in turn until one works.
    ///
    /// AV1 is HARDWARE ONLY, then straight into the H.265 ladder. The software AV1 encoders (libsvtav1 /
    /// libaom-av1) ARE in the bundled build and the Compress pipeline uses them deliberately — but they are
    /// offline encoders, and falling back to them during a realtime capture would trade "smaller files" for
    /// dropped frames. A working H.265 recording plus a warning is the better failure.
    ///
    /// Pure, so the ladder can be asserted without libav.
    /// </summary>
    internal static string[] BuildEncoderLadder(VideoCodec codec, bool preferHardware) => (codec, preferHardware) switch
    {
        (VideoCodec.Av1, true) => [.. HardwareAv1Encoders, .. HardwareH265Encoders, .. SoftwareH265Encoders],
        (VideoCodec.Av1, false) => SoftwareH265Encoders,
        (VideoCodec.H265, true) => [.. HardwareH265Encoders, .. SoftwareH265Encoders],
        (VideoCodec.H265, false) => SoftwareH265Encoders,
        (_, true) => [.. HardwareH264Encoders, .. SoftwareH264Encoders],
        (_, false) => SoftwareH264Encoders,
    };

    /// <summary>Whether <paramref name="encoderName"/> actually emits the codec that was asked for.</summary>
    internal static bool ProducesRequestedCodec(VideoCodec codec, string encoderName) => codec switch
    {
        VideoCodec.Av1 => encoderName.StartsWith("av1_", StringComparison.Ordinal),
        VideoCodec.H265 => encoderName.StartsWith("hevc_", StringComparison.Ordinal) || encoderName == "libx265",
        // H.264 is the floor: everything in its ladder but the last-ditch mpeg4 emits H.264, and mpeg4 never
        // warned before. Left alone so this change cannot alter existing H.264 recordings' messaging.
        _ => true,
    };

    private static string CodecLabel(VideoCodec codec) => codec switch
    {
        VideoCodec.Av1 => "AV1",
        VideoCodec.H265 => "H.265",
        _ => "H.264",
    };

    /// <summary>
    /// Opens the best available video encoder for recording. Hardware / Media-Foundation encoders are
    /// tried first (when <paramref name="preferHardware"/> is set) so the recording is GPU-accelerated
    /// where available; each is test-opened and we fall back to the next, ending with the always-available
    /// software libx264/265. Returns the opened context, or throws if nothing works.
    /// </summary>
    public static AVCodecContext* OpenRecordingEncoderContext(
        VideoCodec codec,
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
        string[] preferredNames = BuildEncoderLadder(codec, preferHardware);

        // The software anchor of whichever ladder we are on: always present in the bundled build, so failing to
        // find it means the build itself is wrong rather than the machine being incapable.
        selectedEncoderName = codec == VideoCodec.H264 ? "libx264" : "libx265";
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
                    if (!ProducesRequestedCodec(codec, candidateName))
                    {
                        warningMessage = $"No {CodecLabel(codec)} encoder available. Falling back to '{candidateName}'.";
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

    #pragma warning disable CS0618
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
    #pragma warning restore CS0618

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
