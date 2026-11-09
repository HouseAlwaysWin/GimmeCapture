using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

internal sealed class LibavVideoFramePlayer : IDisposable
{
    internal readonly record struct MediaInfo(double Duration, int Width, int Height, int Fps, bool HasAudio, int AudioChannels);

    /// <summary>A first frame scaled down for a list row: <see cref="Width"/> × <see cref="Height"/> BGRA pixels, rows packed.</summary>
    internal sealed record BgraThumbnail(int Width, int Height, byte[] Bgra);

    /// <summary>
    /// One decoded frame for <see cref="PlayFramesAsync"/>: outputWidth × outputHeight BGRA pixels with the rows packed,
    /// exactly that many bytes long. It points into the decoder's own memory and is only valid during the call.
    /// </summary>
    public delegate void BgraFrameHandler(ReadOnlySpan<byte> bgra, double seconds);

    // PlayFramesAsync runs again for every seek, segment, thumbnail and frame step, and each run logged its
    // decode path, and every slow open, at Information. Now the decode path is logged when it changes for a codec, and
    // a file that is slow to open once; the repeats go to Debug.
    private static readonly ConcurrentDictionary<AVCodecID, string> LastDecodePathByCodec = new();
    private static readonly ConcurrentDictionary<string, byte> FilesReportedSlowToOpen = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxFilesReportedSlowToOpen = 256;

    /// <summary>
    /// Global opt-out for GPU (D3D11VA) hardware decode: <see cref="VideoDecodeSettings.HardwareDecodeEnabled"/>, which
    /// the settings set. When a file's codec has no D3D11VA config, or hw bring-up/transfer fails, we transparently
    /// fall back to software regardless of this flag.
    /// </summary>
    public static bool HardwareDecodeEnabled
    {
        get => VideoDecodeSettings.HardwareDecodeEnabled;
        set => VideoDecodeSettings.HardwareDecodeEnabled = value;
    }

    /// <summary>
    /// The threads a video decoder gets: libavcodec's own count (0, one a core, frame and slice threads) on the CPU,
    /// one under D3D11VA. FFmpeg sizes a hardware decoder's surface pool for its frame threads too, a surface more for
    /// each, while the GPU does the decoding itself. On a 4K HEVC phone video the Compress editor's decoder held
    /// 1.2-1.3 GB with them and 0.47 GB without, and found a far frame in half the time, 54 ms against 96-111; a step
    /// forward took 19-20 ms instead of 10-14.
    /// </summary>
    internal static int DecodeThreads(bool hardware) => hardware && !FrameThreadsUnderHardwareForTest ? 1 : 0;

    /// <summary>Test seam: the probe sets this to measure the frame threads a hardware decoder had before.</summary>
    internal static volatile bool FrameThreadsUnderHardwareForTest;

    // AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX (libavcodec/codec.h) — stable ABI value; this FFmpeg.AutoGen
    // build does not surface it as a named constant. Bit set in AVCodecHWConfig.methods when a codec can be
    // driven purely by attaching an AVHWDeviceContext (the modern get_format path we use).
    private const int AvCodecHwConfigMethodHwDeviceCtx = 0x01;

    // Real-time catch-up: when software decode (or hw-frame download) can't keep up, a frame whose scheduled
    // wall-clock time is already well in the past is decoded but NOT displayed — we skip the sws_scale + BGRA
    // copy + onFrame marshal (the work that floods the UI thread) and move straight to the next frame. A hard
    // cap on consecutive drops guarantees the picture (and the UI time readout) keeps advancing.
    private const double LateFrameThresholdSeconds = 0.10;
    private const int MaxConsecutiveDrops = 4;

    // How often a loop that holds its first frame for its sound to start looks whether it has.
    private const double SoundStartPollSeconds = 0.005;

    // A single GPU→system frame download (av_hwframe_transfer_data) can fail transiently (e.g. staging exhaustion)
    // — we skip that frame. But a persistent failure (device-removed / TDR) would otherwise freeze on a stale
    // picture forever; past this many consecutive failures we tear the stream down so the caller surfaces it and
    // the user can disable hardware decode.
    internal const int MaxHwTransferFailures = 8;

    // Kept alive for the whole process: Marshal.GetFunctionPointerForDelegate hands libav a native thunk over
    // this instance, and libav invokes it from inside avcodec_open2/decode. If the delegate were collected the
    // thunk would dangle. The callback is state-free (D3D11's pixfmt is constant) so one shared instance is safe
    // even across concurrent PlayAsync calls (e.g. the Compare window decoding two files at once).
    private static readonly unsafe AVCodecContext_get_format GetFormatCallback = GetHwFormat;

    // get_format: prefer the D3D11 hardware surface when the decoder offers it; otherwise take the first
    // (software) format so decode still works. fmt is an AV_PIX_FMT_NONE(-1)-terminated candidate list.
    private static unsafe AVPixelFormat GetHwFormat(AVCodecContext* s, AVPixelFormat* fmt)
    {
        for (AVPixelFormat* p = fmt; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                return *p;
            }
        }

        return fmt[0];
    }

    // True when this frame is so far behind its scheduled wall-clock slot that we should skip displaying it and
    // decode ahead to catch up. Pure/deterministic so it is unit-tested directly. Never drops more than
    // MaxConsecutiveDrops in a row, so playback never goes fully black and the time readout keeps moving.
    internal static bool ShouldDropLateFrame(double elapsedWallSeconds, double startedAtSeconds, double frameSeconds, double speed, int consecutiveDrops)
    {
        if (consecutiveDrops >= MaxConsecutiveDrops)
        {
            return false;
        }

        double safeSpeed = Math.Max(0.1, speed);
        double scheduledWall = Math.Max(0, frameSeconds - startedAtSeconds) / safeSpeed;
        return elapsedWallSeconds - scheduledWall > LateFrameThresholdSeconds;
    }

    // True the first time a codec decodes this way, and whenever its way changes (say the GPU path starts failing).
    internal static bool IsNewDecodePath(ConcurrentDictionary<AVCodecID, string> lastByCodec, AVCodecID codec, string decodePath)
    {
        bool isNew = !lastByCodec.TryGetValue(codec, out string? last) || last != decodePath;
        lastByCodec[codec] = decodePath;
        return isNew;
    }

    // True the first time a file is slow to open. The memory is bounded; once full it starts over.
    internal static bool IsFirstSlowOpen(ConcurrentDictionary<string, byte> reported, string videoPath)
    {
        if (reported.Count >= MaxFilesReportedSlowToOpen)
        {
            reported.Clear();
        }

        return reported.TryAdd(videoPath, 0);
    }

    // Attempts to attach a D3D11VA hardware device to the decoder context. Returns the created device ref (which
    // the caller must free) on success and installs the get_format callback; returns null (and leaves decCtx on
    // the software path) when hw is disabled, the codec has no D3D11VA config, or device creation fails.
    internal static unsafe AVBufferRef* TryInitHardwareDecode(AVCodec* dec, AVCodecContext* decCtx, out string? reason)
    {
        reason = null;
        if (!HardwareDecodeEnabled)
        {
            reason = "disabled by setting";
            return null;
        }

        bool supportsD3d11 = false;
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* cfg = ffmpeg.avcodec_get_hw_config(dec, i);
            if (cfg == null)
            {
                break;
            }

            if ((cfg->methods & AvCodecHwConfigMethodHwDeviceCtx) != 0 &&
                cfg->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
            {
                supportsD3d11 = true;
                break;
            }
        }

        if (!supportsD3d11)
        {
            reason = "codec has no D3D11VA config";
            return null;
        }

        AVBufferRef* hwDeviceCtx = null;
        int rc = ffmpeg.av_hwdevice_ctx_create(&hwDeviceCtx, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
        if (rc < 0 || hwDeviceCtx == null)
        {
            reason = "av_hwdevice_ctx_create failed";
            if (hwDeviceCtx != null)
            {
                ffmpeg.av_buffer_unref(&hwDeviceCtx);
            }

            return null;
        }

        decCtx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDeviceCtx);
        decCtx->get_format = new AVCodecContext_get_format_func { Pointer = Marshal.GetFunctionPointerForDelegate(GetFormatCallback) };
        return hwDeviceCtx;
    }

    public async Task<double?> ProbeDurationSecondsAsync(string videoPath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            FFmpegRuntime.EnsureInitialized();
            unsafe
            {
                AVFormatContext* fmt = null;
                try
                {
                    ThrowIfErr(ffmpeg.avformat_open_input(&fmt, videoPath, null, null), "open_input");
                    ThrowIfErr(ffmpeg.avformat_find_stream_info(fmt, null), "find_stream_info");

                    if (fmt->duration > 0)
                    {
                        return fmt->duration / (double)ffmpeg.AV_TIME_BASE;
                    }

                    int videoStream = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                    if (videoStream >= 0)
                    {
                        var st = fmt->streams[videoStream];
                        if (st->duration > 0)
                        {
                            return st->duration * ffmpeg.av_q2d(st->time_base);
                        }
                    }

                    return (double?)null;
                }
                finally
                {
                    if (fmt != null)
                    {
                        ffmpeg.avformat_close_input(&fmt);
                    }
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>True when the source file contains at least one decodable audio stream.</summary>
    public async Task<bool> ProbeHasAudioAsync(string videoPath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            FFmpegRuntime.EnsureInitialized();
            unsafe
            {
                AVFormatContext* fmt = null;
                try
                {
                    ThrowIfErr(ffmpeg.avformat_open_input(&fmt, videoPath, null, null), "open_input");
                    ThrowIfErr(ffmpeg.avformat_find_stream_info(fmt, null), "find_stream_info");
                    return ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0) >= 0;
                }
                finally
                {
                    if (fmt != null)
                    {
                        ffmpeg.avformat_close_input(&fmt);
                    }
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Coded width/height of the best video stream, or null if there is none.</summary>
    public async Task<(int Width, int Height)?> ProbeVideoSizeAsync(string videoPath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            FFmpegRuntime.EnsureInitialized();
            unsafe
            {
                AVFormatContext* fmt = null;
                try
                {
                    ThrowIfErr(ffmpeg.avformat_open_input(&fmt, videoPath, null, null), "open_input");
                    ThrowIfErr(ffmpeg.avformat_find_stream_info(fmt, null), "find_stream_info");
                    int v = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                    if (v < 0)
                    {
                        return ((int, int)?)null;
                    }

                    var par = fmt->streams[v]->codecpar;
                    return (par->width, par->height);
                }
                finally
                {
                    if (fmt != null)
                    {
                        ffmpeg.avformat_close_input(&fmt);
                    }
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Channel count of the best audio stream, or 0 if there is none.</summary>
    public async Task<int> ProbeAudioChannelsAsync(string videoPath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            FFmpegRuntime.EnsureInitialized();
            unsafe
            {
                AVFormatContext* fmt = null;
                try
                {
                    ThrowIfErr(ffmpeg.avformat_open_input(&fmt, videoPath, null, null), "open_input");
                    ThrowIfErr(ffmpeg.avformat_find_stream_info(fmt, null), "find_stream_info");
                    int a = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                    return a < 0 ? 0 : fmt->streams[a]->codecpar->ch_layout.nb_channels;
                }
                finally
                {
                    if (fmt != null)
                    {
                        ffmpeg.avformat_close_input(&fmt);
                    }
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Single-pass probe: one <c>avformat_open_input</c> + <c>find_stream_info</c> returning duration/size/fps
    /// /audio, instead of the separate <c>Probe*</c> methods each re-opening and re-scanning the file (costly
    /// on large files). fps logic mirrors <c>LibavClipExporter.ResolveFps</c>.
    /// </summary>
    public async Task<MediaInfo> ProbeAsync(string videoPath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            FFmpegRuntime.EnsureInitialized();
            unsafe
            {
                AVFormatContext* fmt = null;
                try
                {
                    (long openMs, long findMs) = OpenForProbe(&fmt, videoPath);
                    if (openMs + findMs > 250)
                    {
                        // The two-part overload: a single event name is cut at 120 characters, which lost the timings.
                        AppLog.Information(
                            "LibavVideoFramePlayer.ProbeAsync",
                            $"slow probe {System.IO.Path.GetFileName(videoPath)}: open_input={openMs}ms find_stream_info={findMs}ms");
                    }

                    return ReadMediaInfo(fmt, out _);
                }
                finally
                {
                    if (fmt != null)
                    {
                        ffmpeg.avformat_close_input(&fmt);
                    }
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What <see cref="ProbeAsync"/> returns, plus the first frame scaled to fit <paramref name="maxEdge"/>
    /// (<see cref="ThumbnailSize"/>), from one open of the file: a compress queue row needs both, and opening the file
    /// again, with a GPU decoder, for a 96 px picture cost more than decoding one frame in software. The thumbnail is null
    /// when the file has no video stream or its first frame cannot be decoded (logged); the probe's answer still stands.
    /// </summary>
    public async Task<(MediaInfo Info, BgraThumbnail? Thumbnail)> ProbeWithThumbnailAsync(
        string videoPath, int maxEdge, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            FFmpegRuntime.EnsureInitialized();
            unsafe
            {
                AVFormatContext* fmt = null;
                try
                {
                    (long openMs, long findMs) = OpenForProbe(&fmt, videoPath);
                    MediaInfo info = ReadMediaInfo(fmt, out int videoStream);

                    var thumbnailStage = Stopwatch.StartNew();
                    BgraThumbnail? thumbnail = null;
                    if (videoStream >= 0 && info.Width > 0 && info.Height > 0)
                    {
                        (int width, int height) = ThumbnailSize(info.Width, info.Height, maxEdge);
                        try
                        {
                            thumbnail = DecodeFirstFrame(fmt, videoStream, width, height, ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            AppLog.Warning("LibavVideoFramePlayer.ProbeWithThumbnailAsync", ex);
                        }
                    }

                    long thumbnailMs = thumbnailStage.ElapsedMilliseconds;
                    if (openMs + findMs + thumbnailMs > 250)
                    {
                        AppLog.Information(
                            "LibavVideoFramePlayer.ProbeWithThumbnailAsync",
                            $"slow probe {System.IO.Path.GetFileName(videoPath)}: open_input={openMs}ms " +
                            $"find_stream_info={findMs}ms thumbnail={thumbnailMs}ms");
                    }

                    return (info, thumbnail);
                }
                finally
                {
                    if (fmt != null)
                    {
                        ffmpeg.avformat_close_input(&fmt);
                    }
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="width"/> × <paramref name="height"/> fitted inside <paramref name="maxEdge"/> with its shape kept,
    /// never enlarged, each side even and at least 2 for the scaler. That is the frame as stored: like the probe's size,
    /// it applies no rotation tag or sample aspect ratio.
    /// </summary>
    internal static (int Width, int Height) ThumbnailSize(int width, int height, int maxEdge)
    {
        // In whole pixels: the scale as a double made 5000 × (96 / 5000) 95.99…, and the long side 2 px short.
        int longer = Math.Max(width, height);
        int thumbnailWidth = longer <= maxEdge ? width : (int)((long)width * maxEdge / longer);
        int thumbnailHeight = longer <= maxEdge ? height : (int)((long)height * maxEdge / longer);
        thumbnailWidth = Math.Max(2, thumbnailWidth);
        thumbnailHeight = Math.Max(2, thumbnailHeight);
        return (thumbnailWidth - (thumbnailWidth & 1), thumbnailHeight - (thumbnailHeight & 1));
    }

    // Opens videoPath with libav's default scan and reads its stream info, timing both: a slow probe of a big or
    // non-faststart file is the moov read in open_input. The caller closes *fmt, also when this throws.
    private static unsafe (long OpenMs, long FindMs) OpenForProbe(AVFormatContext** fmt, string videoPath)
    {
        var stage = Stopwatch.StartNew();
        ThrowIfErr(ffmpeg.avformat_open_input(fmt, videoPath, null, null), "open_input");
        long openMs = stage.ElapsedMilliseconds;
        stage.Restart();
        ThrowIfErr(ffmpeg.avformat_find_stream_info(*fmt, null), "find_stream_info");
        return (openMs, stage.ElapsedMilliseconds);
    }

    // The probe's answer for a file whose stream info has been read. videoStream is negative when there is no video.
    private static unsafe MediaInfo ReadMediaInfo(AVFormatContext* fmt, out int videoStream)
    {
        double duration = fmt->duration > 0 ? fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;

        int width = 0, height = 0, fps = 30;
        videoStream = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
        if (videoStream >= 0)
        {
            AVStream* vst = fmt->streams[videoStream];
            width = vst->codecpar->width;
            height = vst->codecpar->height;
            fps = ResolveFps(vst);
            if (duration <= 0 && vst->duration > 0)
            {
                duration = vst->duration * ffmpeg.av_q2d(vst->time_base);
            }
        }

        int a = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
        bool hasAudio = a >= 0;
        int channels = hasAudio ? fmt->streams[a]->codecpar->ch_layout.nb_channels : 0;

        return new MediaInfo(duration, width, height, fps, hasAudio, channels);
    }

    // Decodes the first frame the decoder gives out from where find_stream_info left the file (its start: the packets it
    // read are handed out again), scaled to width × height BGRA as PlayFramesAsync scales. Software on one thread: frame
    // threads would hold that frame back until each had a packet of its own. Null when the stream ends without a frame.
    private static unsafe BgraThumbnail? DecodeFirstFrame(
        AVFormatContext* fmt, int videoStream, int width, int height, CancellationToken ct)
    {
        AVCodecContext* decCtx = null;
        SwsContext* sws = null;
        AVPacket* pkt = null;
        AVFrame* frame = null;
        AVFrame* outFrame = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            AVCodecParameters* par = fmt->streams[videoStream]->codecpar;
            AVCodec* dec = ffmpeg.avcodec_find_decoder(par->codec_id);
            if (dec == null)
                throw new InvalidOperationException($"Decoder not found for {par->codec_id}");

            decCtx = ffmpeg.avcodec_alloc_context3(dec);
            if (decCtx == null)
                throw new OutOfMemoryException("alloc decoder context");
            ThrowIfErr(ffmpeg.avcodec_parameters_to_context(decCtx, par), "par_to_ctx");
            decCtx->thread_count = 1;
            ThrowIfErr(ffmpeg.avcodec_open2(decCtx, dec, null), "open_decoder");

            pkt = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            if (pkt == null || frame == null)
                throw new OutOfMemoryException("alloc packet/frame");

            bool draining = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (!draining)
                {
                    int rr = ffmpeg.av_read_frame(fmt, pkt);
                    if (rr == ffmpeg.AVERROR_EOF)
                    {
                        // A short clip can hold its only frames back until the decoder is told the stream ended.
                        ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, (AVPacket*)null), "send_eof");
                        draining = true;
                    }
                    else if (rr == ffmpeg.AVERROR(11))
                    {
                        Thread.Sleep(1);
                        continue;
                    }
                    else
                    {
                        ThrowIfErr(rr, "read_frame");
                        if (pkt->stream_index != videoStream)
                        {
                            ffmpeg.av_packet_unref(pkt);
                            continue;
                        }

                        int sr = ffmpeg.avcodec_send_packet(decCtx, pkt);
                        ffmpeg.av_packet_unref(pkt);
                        ThrowIfErr(sr, "send_packet");
                    }
                }

                int gr = ffmpeg.avcodec_receive_frame(decCtx, frame);
                if (gr == ffmpeg.AVERROR(11) && !draining)
                {
                    continue; // the decoder wants more packets first
                }

                if (gr == ffmpeg.AVERROR_EOF || gr == ffmpeg.AVERROR(11))
                {
                    return null;
                }

                ThrowIfErr(gr, "receive_frame");

                sws = ffmpeg.sws_getContext(
                    frame->width, frame->height, (AVPixelFormat)frame->format,
                    width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);
                if (sws == null)
                    throw new InvalidOperationException("sws_getContext failed.");
                LibavColorMatrix.ReadAs(sws, frame->colorspace, frame->color_range);

                outFrame = ffmpeg.av_frame_alloc();
                if (outFrame == null)
                    throw new OutOfMemoryException("alloc frame");
                outFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                outFrame->width = width;
                outFrame->height = height;
                ThrowIfErr(ffmpeg.av_frame_get_buffer(outFrame, 1), "out_frame_buffer");
                ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, frame->height, outFrame->data, outFrame->linesize);

                var bgra = new byte[width * height * 4];
                CopyBgraFrame(outFrame, width, height, bgra);
                return new BgraThumbnail(width, height, bgra);
            }
        }
        finally
        {
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (outFrame != null) ffmpeg.av_frame_free(&outFrame);
            if (sws != null) ffmpeg.sws_freeContext(sws);
            if (decCtx != null) ffmpeg.avcodec_free_context(&decCtx);
        }
    }

    // Rounded, clamped frame rate — mirrors LibavClipExporter.ResolveFps so ProbedFps matches encode-time fps.
    private static unsafe int ResolveFps(AVStream* stream)
    {
        double fps = ffmpeg.av_q2d(stream->avg_frame_rate);
        if (fps <= 0 || double.IsNaN(fps))
        {
            fps = ffmpeg.av_q2d(stream->r_frame_rate);
        }
        if (fps <= 0 || double.IsNaN(fps))
        {
            fps = 30;
        }
        return Math.Clamp((int)Math.Round(fps), 1, 120);
    }

    /// <summary>
    /// The first frame at or after <paramref name="seconds"/> as width × height BGRA bytes, from the file opened for it;
    /// null if there is none or it is cancelled. A preview that shows frame after frame keeps a
    /// <see cref="LibavStillFrameDecoder"/> instead, which opens the file once.
    /// </summary>
    public static async Task<byte[]?> DecodeFrameAtAsync(
        string videoPath, double seconds, int width, int height, CancellationToken ct = default)
    {
        using var decoder = new LibavStillFrameDecoder(videoPath, width, height);
        return await decoder.DecodeFrameAtAsync(seconds, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="PlayFramesAsync"/> for callers that want each frame as an array: the same rented array every time,
    /// whose first outputWidth × outputHeight × 4 bytes are the frame (it can be longer). That costs a copy of every
    /// frame into it, which PlayFramesAsync does not make.
    /// </summary>
    public async Task PlayAsync(
        string videoPath,
        int outputWidth,
        int outputHeight,
        double startSeconds,
        double speed,
        bool loop,
        Action<byte[], double> onFrame,
        CancellationToken ct,
        Action<double>? onDurationKnown = null)
    {
        byte[]? frameBuffer = null;
        try
        {
            await PlayFramesAsync(videoPath, outputWidth, outputHeight, startSeconds, speed, loop, (bgra, seconds) =>
            {
                frameBuffer ??= ArrayPool<byte>.Shared.Rent(bgra.Length);
                bgra.CopyTo(frameBuffer);
                onFrame(frameBuffer, seconds);
            }, ct, onDurationKnown).ConfigureAwait(false);
        }
        finally
        {
            if (frameBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(frameBuffer);
            }
        }
    }

    /// <summary>
    /// Decodes <paramref name="videoPath"/> from <paramref name="startSeconds"/>, scaled to outputWidth × outputHeight
    /// BGRA, and hands each frame to <paramref name="onFrame"/> on the decode thread at its presentation time. The
    /// frame is the scaler's own output, not a copy: a caller that keeps it copies what it needs during the call.
    /// <para>With a <paramref name="loopRange"/> it plays that stretch over and over in this one decode, until
    /// cancelled: at the loop's end it seeks back to its start, with the file, the decoder and its GPU device kept, and
    /// the time runs on across the jump (<see cref="LoopPassTimeline"/>). The loop ends where the picture does at the
    /// latest (a NaN end: right there), so a recording whose sound runs on a little does not hold its last frame
    /// through the rest; <paramref name="onLoopKnown"/> hears the loop as it plays before any frame, for the sound to
    /// loop alike. Frames are then shown by <paramref name="soundClock"/> while it plays, at 1×
    /// (<see cref="PlaybackMediaClock"/>), and by a stopwatch otherwise. A file that does not say how long its picture
    /// is, or cannot seek back, cannot be looped that way: it plays to its end once, and the call returns.</para>
    /// </summary>
    public async Task PlayFramesAsync(
        string videoPath,
        int outputWidth,
        int outputHeight,
        double startSeconds,
        double speed,
        bool loop,
        BgraFrameHandler onFrame,
        CancellationToken ct,
        Action<double>? onDurationKnown = null,
        PlaybackLoop? loopRange = null,
        Func<SoundClockReading>? soundClock = null,
        Action<PlaybackLoop>? onLoopKnown = null)
    {
        await Task.Run(() =>
        {
            FFmpegRuntime.EnsureInitialized();
            unsafe
            {
                AVFormatContext* fmt = null;
                AVCodecContext* decCtx = null;
                AVBufferRef* hwDeviceCtx = null;
                LibavBgraConverter? converter = null;
                AVPacket* pkt = null;
                AVFrame* decFrame = null;
                AVFrame* swFrame = null;
                AVFrame* outFrame = null;

                try
                {
                    // Per-stage timing so a slow first-frame can be diagnosed from the log: for a large / non-
                    // faststart MP4 the cost is the moov (index) read inside open_input, which probesize/
                    // analyzeduration do NOT bound. Only logged when the open is actually slow (see below).
                    var openStage = Stopwatch.StartNew();

                    // Bound the container scan so a pathological large file can't stall the open. Values are
                    // >= libav defaults, so well-formed media still detects all streams/duration; they only cap
                    // truly degenerate inputs. probesize in bytes, analyzeduration in microseconds.
                    AVDictionary* openOpts = null;
                    ffmpeg.av_dict_set(&openOpts, "analyzeduration", "5000000", 0);
                    ffmpeg.av_dict_set(&openOpts, "probesize", "15000000", 0);
                    int openRc = ffmpeg.avformat_open_input(&fmt, videoPath, null, &openOpts);
                    ffmpeg.av_dict_free(&openOpts);
                    ThrowIfErr(openRc, "open_input");
                    long openMs = openStage.ElapsedMilliseconds; openStage.Restart();
                    ThrowIfErr(ffmpeg.avformat_find_stream_info(fmt, null), "find_stream_info");
                    long findMs = openStage.ElapsedMilliseconds; openStage.Restart();

                    int videoStream = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                    if (videoStream < 0)
                        throw new InvalidOperationException("No video stream.");

                    AVStream* st = fmt->streams[videoStream];

                    // Surface the duration we just read so callers (e.g. the Pin window) can skip a separate probe
                    // open — halving the scan-to-first-frame cost on large files. Mirrors ProbeDurationSecondsAsync:
                    // prefer container duration, fall back to the video stream's.
                    double dur = fmt->duration > 0
                        ? fmt->duration / (double)ffmpeg.AV_TIME_BASE
                        : (st->duration > 0 ? st->duration * ffmpeg.av_q2d(st->time_base) : 0);
                    if (dur > 0)
                    {
                        onDurationKnown?.Invoke(dur);
                    }

                    // The loop as it plays, ended where the picture ends at the latest, and where it begins. The caller
                    // hears it before any frame, to loop its sound the same way.
                    LoopPassTimeline? timeline = null;
                    if (loopRange is { } requestedLoop)
                    {
                        PlaybackLoop resolved = ResolveLoop(requestedLoop, PictureEndSeconds(st, dur));
                        if (resolved.CanLoop)
                        {
                            startSeconds = resolved.ClampStart(startSeconds);
                            timeline = new LoopPassTimeline(resolved, startSeconds);
                            onLoopKnown?.Invoke(resolved);
                        }
                    }

                    AVCodecParameters* par = st->codecpar;
                    AVCodec* dec = ffmpeg.avcodec_find_decoder(par->codec_id);
                    if (dec == null)
                        throw new InvalidOperationException($"Decoder not found for {par->codec_id}");

                    decCtx = ffmpeg.avcodec_alloc_context3(dec);
                    ThrowIfErr(ffmpeg.avcodec_parameters_to_context(decCtx, par), "par_to_ctx");

                    // GPU decode (D3D11VA): software decode can't sustain real-time for 4K/high-bitrate files.
                    // Attach a hardware device when the codec supports it; get_format then routes frames onto a
                    // GPU surface which we download per-frame below. Any failure leaves decCtx on the software
                    // path untouched.
                    hwDeviceCtx = TryInitHardwareDecode(dec, decCtx, out string? hwReason);
                    bool hardwareDecode = hwDeviceCtx != null;

                    // Multi-core decode on the CPU: single-threaded decode can't keep up with real-time playback of
                    // large/high-res H.264/H.265 files. 0 = auto (one thread per logical core). FFmpeg ANDs
                    // thread_type against the codec's capabilities, so requesting both frame+slice is safe
                    // (a codec that supports only one uses only that one). Under hw decode one thread: the GPU
                    // decodes, and frame threads only grew its surface pool (DecodeThreads).
                    decCtx->thread_count = DecodeThreads(hardwareDecode);
                    decCtx->thread_type = ffmpeg.FF_THREAD_FRAME | ffmpeg.FF_THREAD_SLICE;
                    ThrowIfErr(ffmpeg.avcodec_open2(decCtx, dec, null), "open_decoder");
                    long codecMs = openStage.ElapsedMilliseconds; openStage.Restart();
                    string decodePath = hardwareDecode
                        ? "D3D11VA hardware decode"
                        : $"software decode ({hwReason ?? "hw unavailable"})";
                    string decodePathMessage = $"{decodePath}, {par->codec_id}";
                    if (IsNewDecodePath(LastDecodePathByCodec, par->codec_id, decodePath))
                    {
                        AppLog.Information("LibavVideoFramePlayer.PlayAsync", decodePathMessage);
                    }
                    else
                    {
                        AppLog.Debug("LibavVideoFramePlayer.PlayAsync", decodePathMessage);
                    }

                    pkt = ffmpeg.av_packet_alloc();
                    decFrame = ffmpeg.av_frame_alloc();
                    swFrame = ffmpeg.av_frame_alloc();
                    outFrame = ffmpeg.av_frame_alloc();
                    if (pkt == null || decFrame == null || swFrame == null || outFrame == null)
                        throw new OutOfMemoryException("alloc packet/frame");

                    // Its scalers are built on the first decoded frame: under hw decode the source pixel format is
                    // only known after the GPU→system download (NV12), not from decCtx->pix_fmt (D3D11).
                    converter = new LibavBgraConverter();

                    outFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                    outFrame->width = outputWidth;
                    outFrame->height = outputHeight;
                    ThrowIfErr(ffmpeg.av_frame_get_buffer(outFrame, 1), "out_frame_buffer");

                    if (startSeconds > 0)
                    {
                        Seek(fmt, decCtx, videoStream, st->time_base, startSeconds);
                    }
                    long seekMs = openStage.ElapsedMilliseconds;

                    // One diagnostic line only when the open is slow (so fast opens stay silent). The stage that
                    // dominates tells us why: open_input = moov/index read (large / non-faststart file), the rest is
                    // stream analysis / codec+GPU init / keyframe seek. A file slow to open is slow at every seek and
                    // loop pass, so it is reported once; the repeats go to Debug.
                    long openTotalMs = openMs + findMs + codecMs + seekMs;
                    if (openTotalMs > 250)
                    {
                        string slowOpenMessage =
                            $"slow open {System.IO.Path.GetFileName(videoPath)} " +
                            $"{par->width}x{par->height} {par->codec_id} hw={hardwareDecode}: total={openTotalMs}ms " +
                            $"open_input={openMs}ms find_stream_info={findMs}ms codec/hw={codecMs}ms seek={seekMs}ms";
                        if (IsFirstSlowOpen(FilesReportedSlowToOpen, videoPath))
                        {
                            AppLog.Information("LibavVideoFramePlayer.PlayAsync", slowOpenMessage);
                        }
                        else
                        {
                            AppLog.Debug("LibavVideoFramePlayer.PlayAsync", slowOpenMessage);
                        }
                    }

                    RunFrames(
                        new OpenDecode
                        {
                            Input = fmt,
                            Decoder = decCtx,
                            VideoStream = videoStream,
                            Packet = pkt,
                            Decoded = decFrame,
                            Downloaded = swFrame,
                            Bgra = outFrame,
                        },
                        converter, outputWidth, outputHeight, startSeconds, speed, loop, onFrame, ct, timeline, soundClock);
                }
                finally
                {
                    if (pkt != null) ffmpeg.av_packet_free(&pkt);
                    if (decFrame != null) ffmpeg.av_frame_free(&decFrame);
                    if (swFrame != null) ffmpeg.av_frame_free(&swFrame);
                    if (outFrame != null) ffmpeg.av_frame_free(&outFrame);
                    converter?.Dispose();
                    if (decCtx != null) ffmpeg.avcodec_free_context(&decCtx);
                    if (hwDeviceCtx != null) ffmpeg.av_buffer_unref(&hwDeviceCtx);
                    if (fmt != null) ffmpeg.avformat_close_input(&fmt);
                }
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>A file and its decoder, already open, and the frames to decode into, for <see cref="RunFrames"/>.</summary>
    internal readonly unsafe struct OpenDecode
    {
        public required AVFormatContext* Input { get; init; }

        public required AVCodecContext* Decoder { get; init; }

        public required int VideoStream { get; init; }

        public required AVPacket* Packet { get; init; }

        public required AVFrame* Decoded { get; init; }

        public required AVFrame* Downloaded { get; init; }

        public required AVFrame* Bgra { get; init; }
    }

    /// <summary>
    /// Decodes on from where <paramref name="open"/> stands and shows each frame at its time, until the end or
    /// <paramref name="ct"/>: <see cref="PlayFramesAsync"/>'s work once the file is open, and a
    /// <see cref="LibavStillFrameDecoder"/>'s when it plays. A caller that has decoded with them before says
    /// <paramref name="framesMayBeWaiting"/>.
    /// <para>With <paramref name="pieces"/> it plays those one after the other, each at its own speed: at a piece's end it
    /// seeks to the next, or decodes on when that is nearer, and the next piece's first frame shows when the last of the
    /// piece before has had its time, or as soon as it is decoded if that is later. <paramref name="onPieceStarts"/> hears
    /// each piece as the decode is about to reach it, on the decode thread, and returns its sound's clock, if it has a
    /// sound, for the picture to keep to at 1× (<see cref="PlaybackMediaClock"/>).</para>
    /// </summary>
    /// <returns>The time of the last frame out of the decoder, NaN when the next frame needs a seek first.</returns>
    internal static unsafe double RunFrames(
        in OpenDecode open,
        LibavBgraConverter converter,
        int outputWidth,
        int outputHeight,
        double startSeconds,
        double speed,
        bool loop,
        BgraFrameHandler onFrame,
        CancellationToken ct,
        LoopPassTimeline? timeline = null,
        Func<SoundClockReading>? soundClock = null,
        PlaybackPieces? pieces = null,
        Func<PlaybackPiece, Func<SoundClockReading>?>? onPieceStarts = null,
        bool framesMayBeWaiting = false)
    {
        AVFormatContext* fmt = open.Input;
        AVCodecContext* decCtx = open.Decoder;
        int videoStream = open.VideoStream;
        AVStream* st = fmt->streams[videoStream];
        AVPacket* pkt = open.Packet;
        AVFrame* decFrame = open.Decoded;
        AVFrame* swFrame = open.Downloaded;
        AVFrame* outFrame = open.Bgra;
        double lastDecoded = double.NaN;

        double loopStartSeconds = Math.Max(0, startSeconds);
        int frameSize = outputWidth * outputHeight * 4;
        int packedRowBytes = outputWidth * 4;
        byte[]? packedFrame = null; // only if the scaler's rows ever come padded
        bool decoderDraining = false;
        bool shouldResetLoop = false;
        bool passOver = false;
        bool pieceOver = false;
        double frameAfterPiece = -1;  // the time of the frame that said so, when one did
        double lastPieceEndsAt = -1; // by playbackClock, until the first frame of the piece after it
        double startedAtSeconds = -1;
        int consecutiveDrops = 0;
        int hwTransferFailures = 0;
        var playbackClock = Stopwatch.StartNew();
        var frameWaiter = FrameDeadlineWaiter.Create(ct);

        // A loop's frames and a preview's pieces are shown by the sound's clock, or a stopwatch; the first frame at once.
        PlaybackMediaClock? mediaClock = timeline != null || pieces != null ? new PlaybackMediaClock(speed) : null;
        Func<SoundClockReading>? readSound = FollowedAt(
            speed, pieces != null && onPieceStarts != null ? onPieceStarts(pieces.Current) : soundClock);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Frames still in the decoder come out before it is given more: it would not take the next packet.
                if (!decoderDraining && !framesMayBeWaiting)
                {
                    int rr = ffmpeg.av_read_frame(fmt, pkt);
                    if (rr == ffmpeg.AVERROR_EOF)
                    {
                        ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, (AVPacket*)null), "send_eof");
                        decoderDraining = true;
                        continue;
                    }

                    if (rr < 0)
                    {
                        if (rr == ffmpeg.AVERROR(11))
                        {
                            Thread.Sleep(1);
                            continue;
                        }

                        ThrowIfErr(rr, "read_frame");
                    }

                    if (pkt->stream_index != videoStream)
                    {
                        ffmpeg.av_packet_unref(pkt);
                        continue;
                    }

                    ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, pkt), "send_packet");
                    ffmpeg.av_packet_unref(pkt);
                }

                framesMayBeWaiting = false;
                while (!ct.IsCancellationRequested)
                {
                    int gr = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
                    if (gr == ffmpeg.AVERROR(11))
                    {
                        break;
                    }

                    if (gr == ffmpeg.AVERROR_EOF)
                    {
                        shouldResetLoop = loop && timeline == null;
                        passOver = timeline != null;
                        pieceOver = pieces != null;
                        break;
                    }

                    ThrowIfErr(gr, "receive_frame");

                    long ts = decFrame->best_effort_timestamp;
                    double seconds = ts >= 0 ? ts * ffmpeg.av_q2d(st->time_base) : 0;
                    lastDecoded = seconds;

                    if (mediaClock != null)
                    {
                        if (timeline?.IsBeforePassStart(seconds) ?? seconds + 0.0005 < startSeconds)
                        {
                            continue;
                        }

                        if (timeline?.EndsPass(seconds) == true)
                        {
                            passOver = true;
                            break;
                        }

                        if (pieces?.EndsPiece(seconds) == true)
                        {
                            pieceOver = true;
                            frameAfterPiece = seconds;
                            break;
                        }

                        double due = timeline?.PlaybackSeconds(seconds) ?? seconds - startSeconds;
                        if (startedAtSeconds < 0)
                        {
                            if (lastPieceEndsAt >= 0)
                            {
                                DelayUntilFrame(frameWaiter, playbackClock, 0, lastPieceEndsAt, 1.0, ct);
                                lastPieceEndsAt = -1;
                                if (ct.IsCancellationRequested)
                                {
                                    break;
                                }
                            }

                            startedAtSeconds = seconds;
                            playbackClock.Restart();
                            mediaClock.Begin(0, due, readSound?.Invoke() ?? SoundClockReading.Unavailable);
                        }
                        else
                        {
                            double now = mediaClock.Now(
                                playbackClock.Elapsed.TotalSeconds, readSound?.Invoke() ?? SoundClockReading.Unavailable);
                            if (IsLateFrame(now, due, consecutiveDrops))
                            {
                                consecutiveDrops++;
                                continue;
                            }

                            consecutiveDrops = 0;
                            DelayUntilPlaybackTime(frameWaiter, playbackClock, mediaClock, readSound, due, speed, ct);
                            if (ct.IsCancellationRequested)
                            {
                                break;
                            }
                        }
                    }
                    else
                    {
                        if (seconds + 0.0005 < startSeconds)
                        {
                            continue;
                        }

                        if (startedAtSeconds < 0)
                        {
                            startedAtSeconds = seconds;
                            playbackClock.Restart();
                        }

                        // Real-time catch-up: if we've already fallen behind this frame's scheduled slot,
                        // skip displaying it (decode still ran, so reference frames stay intact) instead of
                        // rendering late frames that pile onto the UI thread. Capped so the picture — and
                        // the caller's time readout — keeps advancing.
                        if (ShouldDropLateFrame(playbackClock.Elapsed.TotalSeconds, startedAtSeconds, seconds, speed, consecutiveDrops))
                        {
                            consecutiveDrops++;
                            continue;
                        }

                        consecutiveDrops = 0;

                        DelayUntilFrame(frameWaiter, playbackClock, startedAtSeconds, seconds, speed, ct);
                        if (ct.IsCancellationRequested)
                        {
                            break;
                        }
                    }

                    // Under hw decode the frame lives on a GPU surface (AV_PIX_FMT_D3D11); download it
                    // to system memory (NV12) before scaling. Software frames are used directly.
                    AVFrame* srcFrame = decFrame;
                    if (decFrame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
                    {
                        ffmpeg.av_frame_unref(swFrame);
                        int tr = ffmpeg.av_hwframe_transfer_data(swFrame, decFrame, 0);
                        if (tr < 0)
                        {
                            // Transient failure: skip just this frame. Persistent failure (device-removed):
                            // tear down so the caller can surface it and the user can turn hw decode off —
                            // otherwise we would freeze on a stale picture with the clock stuck.
                            if (++hwTransferFailures >= MaxHwTransferFailures)
                            {
                                AppLog.Information($"LibavVideoFramePlayer.PlayAsync hwframe transfer failing persistently (rc={tr}); tearing down");
                                throw FFmpegErrors.ToException(tr, "hwframe_transfer_data");
                            }

                            continue;
                        }

                        hwTransferFailures = 0;
                        srcFrame = swFrame;
                    }

                    // Keyed on the true source format and size, only known here for the hw path (the
                    // downloaded NV12), and set up again if a stream ever changes them. The colours are the
                    // decoded frame's: a download from the GPU carries none.
                    ThrowIfErr(ffmpeg.av_frame_make_writable(outFrame), "out_frame_writable");
                    converter.Convert(srcFrame, decFrame->colorspace, decFrame->color_range, outFrame);

                    // outFrame's buffer was allocated with 1-byte alignment, so its rows are packed and the
                    // scaler's output already is the frame: hand it over without copying it first.
                    ReadOnlySpan<byte> bgra;
                    if (outFrame->linesize[0] == packedRowBytes)
                    {
                        bgra = new ReadOnlySpan<byte>(outFrame->data[0], frameSize);
                    }
                    else
                    {
                        packedFrame ??= ArrayPool<byte>.Shared.Rent(frameSize);
                        CopyBgraFrame(outFrame, outputWidth, outputHeight, packedFrame.AsSpan(0, frameSize));
                        bgra = packedFrame.AsSpan(0, frameSize);
                    }

                    onFrame(bgra, seconds);
                }

                // The loop's pass is over: back to its start, nothing reopened, the time running on. A file
                // that cannot seek there ends the call instead, and its caller plays it pass by pass.
                if (passOver && timeline != null && !ct.IsCancellationRequested)
                {
                    passOver = false;
                    timeline.NextPass();
                    decoderDraining = false;
                    consecutiveDrops = 0;
                    hwTransferFailures = 0;
                    int seekRc = ffmpeg.av_seek_frame(
                        fmt,
                        videoStream,
                        (long)Math.Max(0, timeline.PassStartSeconds / ffmpeg.av_q2d(st->time_base)),
                        ffmpeg.AVSEEK_FLAG_BACKWARD);
                    if (seekRc < 0)
                    {
                        AppLog.Debug("LibavVideoFramePlayer.Loop", $"cannot seek back to the loop's start (rc={seekRc})");
                        break;
                    }

                    ffmpeg.avcodec_flush_buffers(decCtx);
                    continue;
                }

                // The piece is over: on to the next, nothing reopened, while its last frame stays up. A
                // frame just decoded past its end is what said so, ahead of that frame's time, which is
                // when the last one's time is over.
                if (pieceOver && pieces != null && !ct.IsCancellationRequested)
                {
                    pieceOver = false;
                    bool pieceShowedAFrame = startedAtSeconds >= 0;
                    lastPieceEndsAt = pieceShowedAFrame
                        ? Math.Max(0, pieces.Current.EndSeconds - startedAtSeconds) / Math.Max(0.1, speed)
                        : -1;
                    if (!pieces.MoveNext(pieceShowedAFrame))
                    {
                        break;
                    }

                    startSeconds = pieces.Current.StartSeconds;
                    speed = pieces.Current.Speed;
                    startedAtSeconds = -1;
                    consecutiveDrops = 0;
                    hwTransferFailures = 0;
                    mediaClock = new PlaybackMediaClock(speed);

                    // A short way on, the frames between are decoded and not shown, which is sooner than
                    // going back to a keyframe to decode them and the ones before them again.
                    framesMayBeWaiting = !decoderDraining && frameAfterPiece >= 0 && startSeconds > frameAfterPiece + 0.0005
                        && LibavStillFrameDecoder.DecodingOnIsNearer(st, frameAfterPiece, startSeconds);
                    frameAfterPiece = -1;
                    if (!framesMayBeWaiting)
                    {
                        decoderDraining = false;
                        lastDecoded = double.NaN;
                        Seek(fmt, decCtx, videoStream, st->time_base, startSeconds);
                    }

                    readSound = FollowedAt(speed, onPieceStarts?.Invoke(pieces.Current));
                    continue;
                }

                if (shouldResetLoop && !ct.IsCancellationRequested)
                {
                    decoderDraining = false;
                    shouldResetLoop = false;
                    startedAtSeconds = -1;
                    consecutiveDrops = 0;
                    hwTransferFailures = 0;
                    Seek(fmt, decCtx, videoStream, st->time_base, loopStartSeconds);
                    continue;
                }

                if (decoderDraining)
                {
                    break;
                }
            }
        }
        finally
        {
            if (packedFrame != null)
            {
                ArrayPool<byte>.Shared.Return(packedFrame);
            }

            frameWaiter.Dispose();
        }

        return decoderDraining ? double.NaN : lastDecoded;
    }

    // A sound is followed at 1× only: at another speed it is retimed, and its clock does not run in the file's time.
    private static Func<SoundClockReading>? FollowedAt(double speed, Func<SoundClockReading>? clock) =>
        Math.Abs(speed - 1.0) < 0.001 ? clock : null;

    private static unsafe void Seek(AVFormatContext* fmt, AVCodecContext* decCtx, int streamIndex, AVRational timeBase, double seconds)
    {
        long ts = (long)Math.Max(0, seconds / ffmpeg.av_q2d(timeBase));
        int sr = ffmpeg.av_seek_frame(fmt, streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ThrowIfErr(sr, "seek_frame");
        ffmpeg.avcodec_flush_buffers(decCtx);
    }

    // Where the picture ends, in the file's time: the end of its last frame as the video stream states it, else the
    // file's length, else 0 when neither is known.
    private static unsafe double PictureEndSeconds(AVStream* st, double fileSeconds)
    {
        if (st->duration > 0)
        {
            double timeBase = ffmpeg.av_q2d(st->time_base);
            double start = st->start_time != ffmpeg.AV_NOPTS_VALUE ? st->start_time * timeBase : 0;
            return start + (st->duration * timeBase);
        }

        return Math.Max(0, fileSeconds);
    }

    /// <summary>
    /// The loop as it plays: the one asked for, ended where the picture ends at the latest. A NaN end is the picture's
    /// end; with that unknown (0) as well, the loop cannot be laid out, and <see cref="PlaybackLoop.CanLoop"/> is false.
    /// </summary>
    internal static PlaybackLoop ResolveLoop(PlaybackLoop requested, double pictureEndSeconds)
    {
        double end = requested.EndSeconds;
        if (pictureEndSeconds > 0)
        {
            end = double.IsNaN(end) ? pictureEndSeconds : Math.Min(end, pictureEndSeconds);
        }

        return requested with { EndSeconds = end };
    }

    // A loop's frame this far behind the clock is skipped to catch up, as ShouldDropLateFrame does without a loop.
    internal static bool IsLateFrame(double nowSeconds, double dueSeconds, int consecutiveDrops) =>
        consecutiveDrops < MaxConsecutiveDrops && nowSeconds - dueSeconds > LateFrameThresholdSeconds;

    // Waits until the loop's clock reaches the frame, on the timer like DelayUntilFrame. While the clock stands still
    // for the sound to start, it looks again every few milliseconds.
    internal static void DelayUntilPlaybackTime(
        FrameDeadlineWaiter waiter,
        Stopwatch playbackClock,
        PlaybackMediaClock mediaClock,
        Func<SoundClockReading>? readSound,
        double dueSeconds,
        double speed,
        CancellationToken ct)
    {
        double safeSpeed = Math.Max(0.1, speed);
        while (!ct.IsCancellationRequested)
        {
            double now = mediaClock.Now(
                playbackClock.Elapsed.TotalSeconds, readSound?.Invoke() ?? SoundClockReading.Unavailable);
            double remainingSeconds = (dueSeconds - now) / safeSpeed;
            if (remainingSeconds <= FrameDeadlineWaiter.ToleranceSeconds)
            {
                return;
            }

            waiter.Wait(TimeSpan.FromSeconds(
                mediaClock.IsWaitingForSound ? Math.Min(remainingSeconds, SoundStartPollSeconds) : remainingSeconds));
        }
    }

    // Waits on a timer and never spins; see FrameDeadlineWaiter for why.
    internal static void DelayUntilFrame(
        FrameDeadlineWaiter waiter, Stopwatch playbackClock, double startedAtSeconds, double frameSeconds, double speed, CancellationToken ct)
    {
        double safeSpeed = Math.Max(0.1, speed);
        double targetSeconds = Math.Max(0, frameSeconds - startedAtSeconds) / safeSpeed;

        while (!ct.IsCancellationRequested)
        {
            double remainingSeconds = targetSeconds - playbackClock.Elapsed.TotalSeconds;
            if (remainingSeconds <= FrameDeadlineWaiter.ToleranceSeconds)
            {
                return;
            }

            waiter.Wait(TimeSpan.FromSeconds(remainingSeconds));
        }
    }

    internal static unsafe void CopyBgraFrame(AVFrame* frame, int width, int height, Span<byte> output)
    {
        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
        {
            byte* src = frame->data[0] + (y * frame->linesize[0]);
            Span<byte> destination = output.Slice(y * rowBytes, rowBytes);
            fixed (byte* dst = destination)
            {
                Buffer.MemoryCopy(src, dst, rowBytes, rowBytes);
            }
        }
    }

    private static void ThrowIfErr(int err, string ctx)
    {
        if (err < 0) throw FFmpegErrors.ToException(err, ctx);
    }

    public void Dispose()
    {
    }
}
