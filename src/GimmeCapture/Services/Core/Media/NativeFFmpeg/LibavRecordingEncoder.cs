using System;
using System.Collections.Generic;
using System.Linq;
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

    // 0 = not probed yet, 1 = usable, 2 = not usable. Only ever set from a probe that actually ran, in this run or an
    // earlier one (RecallHardwareAv1), so a probe attempted before the native libraries were loaded does not poison the
    // answer for the rest of the session.
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
    /// <summary>
    /// The answer <see cref="HasUsableHardwareAv1Encoder"/> has already reached, or null if it has not run yet.
    /// Never probes: running the probe means loading FFmpeg, so callers on the UI thread ask this instead.
    /// </summary>
    public static bool? KnownHardwareAv1Usable => System.Threading.Volatile.Read(ref _hardwareAv1State) switch
    {
        1 => true,
        2 => false,
        _ => null
    };

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

    /// <summary>
    /// Takes the answer a probe reached in an earlier run on this machine as it is now (<see cref="HardwareCapabilityCache"/>),
    /// so the Record tab can list the codecs without loading FFmpeg. A probe that already ran in this run wins. A stale
    /// "usable" costs no recording: an AV1 recording still falls down its encoder ladder when the open fails.
    /// </summary>
    internal static void RecallHardwareAv1(bool usable)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _hardwareAv1State, usable ? 1 : 2, 0) == 0)
        {
            AppLog.Information($"Recording.HardwareAv1Probe: {(usable ? "available" : "unavailable")} (remembered)");
        }
    }

    // Uncached, for a test to measure; everything else asks HasUsableHardwareAv1Encoder.
    internal static bool ProbeHardwareAv1()
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

            foreach (bool gpuFrames in ProbeFrameAttempts(candidateName))
            {
                AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(candidate);
                if (ctx == null)
                {
                    break;
                }

                AVDictionary* opts = null;
                try
                {
                    // On 4:2:0, which a recording falls back to on NVENC as well, so BGRA cannot decide the answer.
                    ConfigureEncoderContext(
                        ctx, candidate, candidateName, ProbeWidth, ProbeHeight, ProbeFps, ProbeCrf, 0, &opts, allowBgra: false);
                    if (gpuFrames && !TryGiveGpuFrames(ctx))
                    {
                        continue;
                    }

                    int opened = ffmpeg.avcodec_open2(ctx, candidate, &opts);
                    if (opened >= 0)
                    {
                        LogNative($"Hardware AV1 encoder usable: {candidateName}");
                        return true;
                    }

                    if (gpuFrames && opened == GpuLacksIt)
                    {
                        // The GPU has no AV1 encoder, and would not have one on CUDA either.
                        break;
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
        }

        return false;
    }

    // NVENC's answer for a codec the GPU lacks: AVERROR(ENOSYS), and ENOSYS is 40 on Windows.
    private const int GpuLacksIt = -40;

    private static readonly bool[] GpuFramesFirst = [true, false];
    private static readonly bool[] MemoryFramesOnly = [false];

    /// <summary>
    /// How the AV1 probe opens <paramref name="encoderName"/>: NVENC, on Windows, first on Direct3D 11 frames
    /// (<see cref="TryGiveGpuFrames"/>) and then as a recording opens it; any other encoder only as a recording does.
    /// </summary>
    internal static bool[] ProbeFrameAttempts(string encoderName) =>
        OperatingSystem.IsWindows() && encoderName.EndsWith("_nvenc", StringComparison.Ordinal)
            ? GpuFramesFirst
            : MemoryFramesOnly;

    /// <summary>
    /// Has an NVENC encoder take Direct3D 11 frames on the NVIDIA adapter, for the AV1 probe, which opens the encoder
    /// and encodes nothing. Given frames in memory, FFmpeg's NVENC makes a CUDA context of its own, and the CUDA driver
    /// keeps its memory once it has: the probe left the process 190 MB larger for good the first time the Record tab
    /// opened, though nothing was recorded. On Direct3D 11 frames it left 34 MB. Recordings keep frames in memory:
    /// once NVENC has encoded frames of a real capture's size the driver keeps 200-260 MB either way (3968x1160, 120
    /// frames: 208-266 MB on CUDA, 198-256 on Direct3D 11), and making the device took 140-330 ms of every start.
    /// Returns false, leaving the context as it was, where there is no NVIDIA adapter to make a device on.
    /// </summary>
    private static bool TryGiveGpuFrames(AVCodecContext* ctx)
    {
        AVBufferRef* device = null;
        AVDictionary* deviceOptions = null;
        // The NVIDIA adapter, which on a laptop with two GPUs is not the default one.
        ffmpeg.av_dict_set(&deviceOptions, "vendor_id", "0x10de", 0);
        int created = ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, deviceOptions, 0);
        ffmpeg.av_dict_free(&deviceOptions);
        if (created < 0 || device == null)
        {
            LogNative($"No Direct3D 11 device for NVENC: {FFmpegErrors.Describe(created)} ({created})");
            return false;
        }

        // The frames context holds a reference of its own to the device.
        AVBufferRef* frames = ffmpeg.av_hwframe_ctx_alloc(device);
        ffmpeg.av_buffer_unref(&device);
        if (frames == null)
        {
            return false;
        }

        var framesContext = (AVHWFramesContext*)frames->data;
        framesContext->format = AVPixelFormat.AV_PIX_FMT_D3D11;
        framesContext->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        framesContext->width = ctx->width;
        framesContext->height = ctx->height;
        int initialised = ffmpeg.av_hwframe_ctx_init(frames);
        if (initialised < 0)
        {
            LogNative($"No Direct3D 11 frames for NVENC: {FFmpegErrors.Describe(initialised)} ({initialised})");
            ffmpeg.av_buffer_unref(&frames);
            return false;
        }

        // The encoder takes this reference over, and avcodec_free_context lets go of it.
        ctx->pix_fmt = AVPixelFormat.AV_PIX_FMT_D3D11;
        ctx->hw_frames_ctx = frames;
        return true;
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

            // NVENC is opened on the capture's BGRA first (ChooseEncoderPixelFormat), and again on the 4:2:0 it took
            // before should its driver refuse BGRA, rather than the recording losing the GPU encoder.
            foreach (bool allowBgra in BgraAttempts(candidateName))
            {
                AVCodecContext* candidateCtx = ffmpeg.avcodec_alloc_context3(candidate);
                if (candidateCtx == null)
                {
                    LogNative($"Encoder candidate alloc failed: {candidateName}");
                    break;
                }

                AVDictionary* candidateOpts = null;
                try
                {
                    ConfigureEncoderContext(
                        candidateCtx, candidate, candidateName, width, height, fps, crf, bitrateOverride, &candidateOpts, allowBgra);
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

                    lastOpenError =
                        $"{candidateName} ({ffmpeg.av_get_pix_fmt_name(candidateCtx->pix_fmt)}): {FFmpegErrors.Describe(openResult)} ({openResult})";
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
        }

        if (!foundRequestedEncoder)
        {
            throw new InvalidOperationException($"Encoder '{requestedEncoderName}' unavailable.");
        }

        throw new InvalidOperationException($"No compatible video encoder available. Last error: {lastOpenError ?? "unknown"}");
    }

    /// <summary>
    /// Loads what the encoder a recording of <paramref name="codec"/> would get loads the first time it opens, before a
    /// recording waits on it, and returns what it did: <c>cuInit</c>, or the name of the encoder it opened and freed.
    ///
    /// NVENC is not opened. On frames in memory it makes a CUDA context, and once a process has made one the CUDA driver
    /// keeps 190 MB of it for good, whether anything is recorded or not. Only starting the driver kept 8 MB, and took a
    /// recording's first NVENC open from 144-200 ms to 122-130 (89-95 after an opened NVENC, 86-108 on every later one;
    /// 1280x720, fresh processes). NVENC_PROBE in <c>LibavRecordingEncoderTests</c> checks what this keeps.
    /// </summary>
    public static string WarmUpEncoder(VideoCodec codec, bool preferHardware)
    {
        if (WarmsUpThroughCuda(codec, preferHardware) && CudaDriver.TryInitialize())
        {
            return "cuInit";
        }

        // Software only, or no NVIDIA driver to start: NVENC then fails to open, and a recording goes on down its ladder
        // as this does.
        AVDictionary* options = null;
        AVCodecContext* context = OpenRecordingEncoderContext(
            codec, preferHardware, 640, 480, 30, 0, 0, &options, out string name, out _);
        ffmpeg.av_dict_free(&options);
        ffmpeg.avcodec_free_context(&context);
        return name;
    }

    /// <summary>Whether the encoder a recording tries first is NVENC, which the warm-up does not open. Pure.</summary>
    internal static bool WarmsUpThroughCuda(VideoCodec codec, bool preferHardware) =>
        BuildEncoderLadder(codec, preferHardware)[0].EndsWith("_nvenc", StringComparison.Ordinal);

    // Internal so a test can check what it sets without opening an encoder.
    internal static void ConfigureEncoderContext(
        AVCodecContext* encCtx,
        AVCodec* enc,
        string encoderName,
        int width,
        int height,
        int fps,
        int crf,
        long bitrateOverride,
        AVDictionary** encOpts,
        bool allowBgra = true)
    {
        encCtx->codec_id = enc->id;
        encCtx->width = width;
        encCtx->height = height;
        encCtx->pix_fmt = ChooseEncoderPixelFormat(enc, encoderName, allowBgra);
        if (encCtx->pix_fmt == AVPixelFormat.AV_PIX_FMT_BGRA)
        {
            // NVENC converts BGRA to limited-range BT.601 and writes that into the stream whatever it is told; the
            // container is told the same.
            encCtx->colorspace = AVColorSpace.AVCOL_SPC_BT470BG;
            encCtx->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        }
        encCtx->time_base = new AVRational { num = 1, den = fps };
        encCtx->framerate = new AVRational { num = fps, den = 1 };
        encCtx->gop_size = fps * 2;
        encCtx->max_b_frames = 0;
        encCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        // Through the API libavcodec gives an encoder one thread unless asked for more. libx264 asks for auto itself (and
        // keeps zerolatency's sliced threads), and libx265 under zerolatency keeps to one frame thread whatever it is told,
        // spreading each frame over the cores by rows; but the last-ditch libopenh264 and mpeg4 ran on a single thread.
        // Hardware and Media Foundation encoders are left as they are.
        if (!IsHardwareOrMediaFoundationEncoder(encoderName))
        {
            encCtx->thread_count = 0;
        }

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
    private static AVPixelFormat ChooseEncoderPixelFormat(AVCodec* enc, string encoderName, bool allowBgra)
    {
        var supported = new List<AVPixelFormat>();
        for (int i = 0; enc->pix_fmts != null && enc->pix_fmts[i] != AVPixelFormat.AV_PIX_FMT_NONE; i++)
        {
            supported.Add(enc->pix_fmts[i]);
        }

        return ChooseEncoderPixelFormat(supported, encoderName, allowBgra);
    }
    #pragma warning restore CS0618

    /// <summary>
    /// The pixel format a recording hands <paramref name="encoderName"/>, from the formats it lists (none when it lists
    /// none). Every recording source hands over BGRA: gdigrab's bitmaps, WGC's textures, the composite's canvas. NVENC
    /// takes BGRA as it is and converts it to 4:2:0 on the GPU, to the colours the CPU's conversion gave (BT.601: eight
    /// colour patches decode within one level of each other) and closer to the screen (RGB PSNR 0.15-5.3 dB higher on
    /// four of the user's recordings at the same bitrate), and it writes that matrix into the stream, which the 4:2:0
    /// converted on the CPU left unset, so players took BT.709 for a picture 720 lines or taller and shifted its colours.
    /// That conversion, on the recording thread, cost most of a recording's CPU: a 1450x900 region at 30 fps on
    /// hevc_nvenc took 1130-1260 M cycles a second, 700-890 handed BGRA; the whole 1920x1080 screen 1910-1920, dropping
    /// 3-7 frames in 10 s, against 1290-1420 with none dropped (RTX 3050 Laptop, 2026-11-05). Other encoders get the
    /// first 4:2:0 format they list, as before. Pure, so the choice can be asserted without libav.
    /// </summary>
    internal static AVPixelFormat ChooseEncoderPixelFormat(
        IReadOnlyList<AVPixelFormat> supported, string encoderName, bool allowBgra = true)
    {
        if (allowBgra && TakesBgra(encoderName) && supported.Contains(AVPixelFormat.AV_PIX_FMT_BGRA))
        {
            return AVPixelFormat.AV_PIX_FMT_BGRA;
        }

        AVPixelFormat[] preferredFormats = IsHardwareOrMediaFoundationEncoder(encoderName)
            ? [AVPixelFormat.AV_PIX_FMT_NV12, AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_P010LE]
            : [AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_NV12];

        if (supported.Count == 0)
        {
            return preferredFormats[0];
        }

        foreach (AVPixelFormat format in supported)
        {
            if (Array.IndexOf(preferredFormats, format) >= 0)
            {
                return format;
            }
        }

        return supported[0];
    }

    private static bool TakesBgra(string encoderName) => encoderName.EndsWith("_nvenc", StringComparison.Ordinal);

    private static readonly bool[] BgraThen420 = [true, false];
    private static readonly bool[] Only420 = [false];

    /// <summary>How a recording opens <paramref name="encoderName"/>: NVENC on BGRA, then on 4:2:0; any other once.</summary>
    internal static bool[] BgraAttempts(string encoderName) => TakesBgra(encoderName) ? BgraThen420 : Only420;

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
        AppLog.Information("RecordingNative", message); // was Debug.WriteLine, which a release build leaves out
    }
}
