using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FFmpeg.AutoGen;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using NAudio.Wave;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// In-process (libav, no ffmpeg.exe) frame-accurate trim/concat export for the floating video pin.
/// Re-encodes the KEPT source ranges into one continuous H.264/H.265 clip (audio re-encoded to AAC and muxed),
/// so cut points are exact regardless of keyframe spacing. Supports an optional crop rect and an optional
/// per-frame SkiaSharp composite hook (annotation/redaction burn-in: decode → BGRA → draw → YUV → encode).
/// GIF/WebM targets are handled elsewhere.
///
/// Video: seek to each range start, drop decoded frames before the start, re-encode frames within
/// [start,end) with a continuous output PTS (CFR at the source frame rate). Audio: decode each range to
/// PCM (truncated to the range length), pitch-preservingly retime any non-1× segment via
/// <see cref="LibavAtempoFilter"/>, concatenate, encode to AAC, then mux with the video.
/// </summary>
internal static class LibavClipExporter
{
    // Speed > 1 = faster (fewer output frames), < 1 = slow motion (duplicated frames). 1.0 = unchanged.
    internal readonly record struct SourceRange(double StartSeconds, double EndSeconds, double Speed = 1.0)
    {
        public double Duration => Math.Max(0, EndSeconds - StartSeconds);
        public double EffectiveSpeed => Speed > 0.01 ? Speed : 1.0;
    }

    private const double TimeEpsilon = 1e-4;

    /// <summary>
    /// Advances the fractional output-frame cursor by one source frame at the given speed and returns how
    /// many output frames to emit for it. Faster-than-1× drops frames (can return 0); slower duplicates
    /// (can return ≥2); 1× always returns 1. Pure so the retiming math can be unit-tested.
    /// </summary>
    internal static int AdvanceFrameCursor(ref double accum, double speed)
    {
        double effective = speed > 0.01 ? speed : 1.0;
        double before = accum;
        accum += 1.0 / effective;
        // Nudge before flooring so accumulated FP drift (e.g. 90 × 1/1.5 = 59.999…) doesn't spuriously
        // drop a frame at boundaries; the epsilon telescopes across calls and can't over-count exact values.
        const double eps = 1e-9;
        return (int)Math.Floor(accum + eps) - (int)Math.Floor(before + eps);
    }

    /// <summary>
    /// Downscales (width,height) so the height is at most <paramref name="maxHeight"/>, preserving aspect
    /// and snapping to even dimensions (required by YUV420P). Returns the dims unchanged when
    /// <paramref name="maxHeight"/> is 0 or the source is already short enough, so existing callers that
    /// don't downscale are byte-for-byte unaffected. Pure, so the math is unit-testable.
    /// </summary>
    internal static (int Width, int Height) ScaleToMaxHeight(int width, int height, int maxHeight)
    {
        if (maxHeight <= 0 || width <= 0 || height <= 0 || height <= maxHeight)
        {
            return (width, height);
        }

        double scale = (double)maxHeight / height;
        int w = (int)Math.Round(width * scale);
        return (Math.Max(2, w - (w & 1)), Math.Max(2, maxHeight - (maxHeight & 1)));
    }

    /// <summary>Snaps a rotation to 0/90/180/270 (clockwise); anything else falls back to 0.</summary>
    internal static int NormalizeRotation(int degrees)
    {
        int r = ((degrees % 360) + 360) % 360;
        return r is 90 or 180 or 270 ? r : 0;
    }

    /// <summary>Maps an output file extension to the libav container name, or null if unsupported here.</summary>
    public static string? ContainerForExtension(string extension)
    {
        string ext = extension.StartsWith('.') ? extension[1..] : extension;
        return ext.ToLowerInvariant() switch
        {
            "mp4" or "m4v" => "mp4",
            "mkv" => "matroska",
            "mov" => "mov",
            _ => null, // gif/webm/etc. handled by the dedicated transcoders, not this path
        };
    }

    /// <summary>
    /// Exports the kept <paramref name="ranges"/> of <paramref name="inputPath"/> to
    /// <paramref name="outputPath"/>. Returns true on success; throws on a libav/IO failure.
    /// </summary>
    /// <param name="frameComposite">
    /// Optional per-frame hook: receives each decoded frame as a BGRA <see cref="SKBitmap"/> (source
    /// resolution) plus that frame's source time in seconds, to draw onto in place (annotation/redaction
    /// burn-in; the time lets time-varying composites position themselves), before it is re-encoded.
    /// </param>
    /// <param name="options">
    /// Optional encode knobs (codec, target bitrate, CRF, preset, downscale, drop-audio). Null reproduces
    /// the original behaviour, so existing callers are unaffected.
    /// </param>
    /// <param name="progress">
    /// Optional progress sink reporting the video-encode fraction (0..1) by source time consumed. The audio
    /// decode/encode + mux tail isn't tracked, so the bar may sit near 1.0 briefly at the end. Null = silent.
    /// </param>
    /// <param name="pauseGate">
    /// Optional pause control: when reset (non-signaled), the video-encode loop blocks between frames until it
    /// is set again (or cancellation fires). Null or always-set = no pausing. Must start signaled.
    /// </param>
    public static bool TryExport(
        string inputPath,
        IReadOnlyList<SourceRange> ranges,
        string outputPath,
        VideoQuality quality,
        VideoEditCrop? crop = null,
        Action<SKBitmap, double>? frameComposite = null,
        CancellationToken cancellationToken = default,
        LibavExportOptions? options = null,
        IProgress<double>? progress = null,
        ManualResetEventSlim? pauseGate = null,
        Action<SKBitmap, double>? frameCompositeAfterTransform = null)
    {
        FFmpegRuntime.EnsureInitialized();
        var opt = options ?? new LibavExportOptions();

        if (string.IsNullOrEmpty(inputPath) || !File.Exists(inputPath))
        {
            throw new FileNotFoundException("Source video not found.", inputPath);
        }

        if (ranges == null || ranges.Count == 0)
        {
            throw new ArgumentException("No ranges to export.", nameof(ranges));
        }

        string ext = Path.GetExtension(outputPath);
        string container = ContainerForExtension(ext)
            ?? throw new NotSupportedException($"In-process clip export does not support '{ext}' yet.");

        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_ClipExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string videoTemp = Path.Combine(tempDir, "video.mp4");

        try
        {
            // True two-pass (libx264 target-size only): pass 1 writes stats to a temp log, pass 2 reads them.
            // H.265 / CRF fall through to the single encode. Progress spans both passes (0..0.5, 0.5..1).
            bool realTwoPass = opt.TwoPass && opt.TargetVideoBitrateKbps > 0 && opt.Codec == VideoCodec.H264;
            if (realTwoPass)
            {
                string statsFile = Path.Combine(tempDir, "x264_2pass.log");
                string pass1Temp = Path.Combine(tempDir, "pass1.mp4");
                EncodeVideoRanges(inputPath, ranges, pass1Temp, quality, crop, frameComposite, cancellationToken,
                    opt, progress == null ? null : new ScaledProgress(progress, 0.0, 0.5), pauseGate, 1, statsFile,
                    frameCompositeAfterTransform);
                EncodeVideoRanges(inputPath, ranges, videoTemp, quality, crop, frameComposite, cancellationToken,
                    opt, progress == null ? null : new ScaledProgress(progress, 0.5, 0.5), pauseGate, 2, statsFile,
                    frameCompositeAfterTransform);
            }
            else
            {
                EncodeVideoRanges(inputPath, ranges, videoTemp, quality, crop, frameComposite, cancellationToken,
                    opt, progress, pauseGate, frameCompositeAfterTransform: frameCompositeAfterTransform);
            }

            string? audioTemp = null;
            bool hasAudio = !opt.DropAudio
                && TryBuildAudio(inputPath, ranges, tempDir, quality, opt, cancellationToken, out audioTemp);

            if (hasAudio && audioTemp != null)
            {
                LibavMuxer.MuxVideoAndAudio(videoTemp, audioTemp, outputPath, container);
            }
            else if (container == "mp4")
            {
                File.Copy(videoTemp, outputPath, true);
            }
            else
            {
                LibavMuxer.RemuxVideo(videoTemp, outputPath, container);
            }

            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Adds audio — decoded from <paramref name="audioSource"/> over <paramref name="ranges"/> — onto an
    /// already-encoded, video-only file and writes the final container. Used by segment-based resume, which
    /// encodes/concatenates the video separately and builds audio once at the end. No video re-encode here.
    /// Returns true when a non-empty output is produced.
    /// </summary>
    public static bool TryMuxAudioForRanges(
        string audioSource,
        IReadOnlyList<SourceRange> ranges,
        string videoPath,
        string outputPath,
        VideoQuality quality,
        LibavExportOptions options,
        CancellationToken cancellationToken)
    {
        FFmpegRuntime.EnsureInitialized();

        string ext = Path.GetExtension(outputPath);
        string container = ContainerForExtension(ext)
            ?? throw new NotSupportedException($"In-process clip export does not support '{ext}' yet.");

        string tempDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_ClipAudio_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string? audioTemp = null;
            bool hasAudio = !options.DropAudio
                && TryBuildAudio(audioSource, ranges, tempDir, quality, options, cancellationToken, out audioTemp);

            if (hasAudio && audioTemp != null)
            {
                LibavMuxer.MuxVideoAndAudio(videoPath, audioTemp, outputPath, container);
            }
            else if (container == "mp4")
            {
                File.Copy(videoPath, outputPath, true);
            }
            else
            {
                LibavMuxer.RemuxVideo(videoPath, outputPath, container);
            }

            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }
    }

    // ── Video: decode the kept ranges and re-encode them into one continuous H.264/H.265 file ──
    private static unsafe void EncodeVideoRanges(
        string inputPath,
        IReadOnlyList<SourceRange> ranges,
        string outputPath,
        VideoQuality quality,
        VideoEditCrop? crop,
        Action<SKBitmap, double>? frameComposite,
        CancellationToken ct,
        LibavExportOptions opt,
        IProgress<double>? progress = null,
        ManualResetEventSlim? pauseGate = null,
        int passNumber = 0,
        string? statsFile = null,
        Action<SKBitmap, double>? frameCompositeAfterTransform = null)
    {
        VideoCodec codec = opt.Codec;
        int targetVideoBitrateKbps = opt.TargetVideoBitrateKbps;
        AVFormatContext* inFmt = null;
        AVCodecContext* decCtx = null;
        AVFormatContext* outFmt = null;
        AVCodecContext* encCtx = null;
        SwsContext* sws = null;
        SwsContext* swsToBgra = null;
        AVPacket* inPkt = null;
        AVPacket* outPkt = null;
        AVFrame* decFrame = null;
        AVFrame* encFrame = null;
        AVFrame* bgraFrame = null;
        AVDictionary* encOpts = null;
        AVStream* outStream = null;
        long frameIndex = 0;
        // Fractional output-frame cursor for per-segment speed: each source frame advances it by
        // 1/speed output frames, and we emit floor(after)-floor(before) frames (drop when >1×, dup when <1×).
        double frameAccum = 0;
        bool annotate = frameComposite != null || frameCompositeAfterTransform != null;

        // Output rotation (0/90/180/270 clockwise), baked into the pixels via a SkiaSharp BGRA round-trip.
        int rotation = NormalizeRotation(opt.RotationDegrees);
        bool rotate = rotation != 0;
        SKBitmap? rotateDst = null; // managed rotation target (rotated dims), reused per frame
        bool needBgra = annotate || rotate;

        // Progress is reported by source-time consumed across all kept ranges (0..1). Throttled to whole
        // percent changes so a long encode doesn't flood the IProgress sink / UI thread.
        double totalSrcDuration = 0;
        foreach (SourceRange r in ranges) totalSrcDuration += r.Duration;
        if (totalSrcDuration <= 0) totalSrcDuration = 1; // guard div-by-zero (unreadable/zero-length)
        double elapsedBeforeRange = 0;
        int lastPercent = -1;

        try
        {
            ThrowIfErr(ffmpeg.avformat_open_input(&inFmt, inputPath, null, null), "clip_open_input");
            ThrowIfErr(ffmpeg.avformat_find_stream_info(inFmt, null), "clip_find_stream_info");
            int videoIn = ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoIn < 0)
            {
                throw new InvalidOperationException("No video stream to export.");
            }

            AVStream* inStream = inFmt->streams[videoIn];
            AVCodec* dec = ffmpeg.avcodec_find_decoder(inStream->codecpar->codec_id);
            if (dec == null)
            {
                throw new InvalidOperationException("Video decoder unavailable for export.");
            }

            decCtx = ffmpeg.avcodec_alloc_context3(dec);
            ThrowIfErr(ffmpeg.avcodec_parameters_to_context(decCtx, inStream->codecpar), "clip_par_to_ctx");
            ThrowIfErr(ffmpeg.avcodec_open2(decCtx, dec, null), "clip_open_decoder");

            int srcFps = ResolveFps(inStream);
            // FPS cap: when a cap below the source rate is requested, the encoder runs at the cap and we
            // decimate source frames into it (output stays CFR). 0 or ≥ source fps keeps the source rate.
            int fps = (opt.MaxFps > 0 && opt.MaxFps < srcFps) ? opt.MaxFps : srcFps;
            // Per-source-frame cursor scaling that realises the decimation: emit ≈ fps/srcFps output frames
            // per source frame. 1.0 when not capping, so the speed-only path is byte-for-byte unchanged.
            double fpsCursorScale = (double)srcFps / fps;

            // H.265 when requested and available; otherwise fall back to H.264 (libx265 may be absent
            // from the bundled build). Existing callers default to H.264 so their output is unchanged.
            AVCodec* enc = null;
            if (codec == VideoCodec.H265)
            {
                enc = ffmpeg.avcodec_find_encoder_by_name("libx265");
                if (enc == null)
                {
                    enc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_HEVC);
                }
            }
            if (enc == null)
            {
                enc = ffmpeg.avcodec_find_encoder_by_name("libx264");
            }
            if (enc == null)
            {
                enc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
            }
            if (enc == null)
            {
                throw new InvalidOperationException("No H.264/H.265 encoder available.");
            }

            ThrowIfErr(ffmpeg.avformat_alloc_output_context2(&outFmt, null, "mp4", outputPath), "clip_alloc_output");
            if (outFmt == null)
            {
                throw new InvalidOperationException("Failed to allocate clip output context.");
            }

            // sws source: the crop rect when cropping, else the full source frame.
            int outW = crop is { } cw ? cw.Width : decCtx->width;
            int outH = crop is { } ch ? ch.Height : decCtx->height;

            // After rotation, 90/270 transpose the frame; downscale is applied to the post-rotation dims.
            bool swap = rotation == 90 || rotation == 270;
            int rotW = swap ? outH : outW;
            int rotH = swap ? outW : outH;
            (int encW, int encH) = ScaleToMaxHeight(rotW, rotH, opt.MaxHeight);

            encCtx = ffmpeg.avcodec_alloc_context3(enc);
            encCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            encCtx->codec_id = enc->id;
            encCtx->width = encW;
            encCtx->height = encH;
            encCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            encCtx->time_base = new AVRational { num = 1, den = fps };
            encCtx->framerate = new AVRational { num = fps, den = 1 };
            encCtx->gop_size = fps * 2;
            encCtx->max_b_frames = 0;
            if ((outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                encCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            ffmpeg.av_dict_set(&encOpts, "preset", string.IsNullOrWhiteSpace(opt.Preset) ? "veryfast" : opt.Preset, 0);
            if (targetVideoBitrateKbps > 0)
            {
                // Average-bitrate mode for "compress to a target size".
                long bps = (long)targetVideoBitrateKbps * 1000;
                encCtx->bit_rate = bps;
                bool twoPass = passNumber > 0;
                if (!twoPass)
                {
                    // Single-pass ABR with a VBV peak cap so the output lands near the requested size
                    // (approximate). Two-pass omits the cap and lets x264 distribute bits from pass-1 stats.
                    encCtx->rc_max_rate = bps;
                    encCtx->rc_buffer_size = (int)Math.Min(int.MaxValue, bps * 2);
                }
                // libx265 reads rate control from x265-params, not the AVCodecContext bitrate fields.
                if (enc->id == AVCodecID.AV_CODEC_ID_HEVC)
                {
                    long kbps = targetVideoBitrateKbps;
                    ffmpeg.av_dict_set(&encOpts, "x265-params",
                        $"bitrate={kbps}:vbv-maxrate={kbps}:vbv-bufsize={kbps * 2}", 0);
                }
                else if (twoPass && statsFile != null)
                {
                    // True two-pass for libx264: pass 1 writes stats, pass 2 reads them. The "stats" AVOption
                    // takes a full path (unlike x265-params' colon-delimited string), so Windows paths work.
                    encCtx->flags |= passNumber == 1 ? ffmpeg.AV_CODEC_FLAG_PASS1 : ffmpeg.AV_CODEC_FLAG_PASS2;
                    ffmpeg.av_dict_set(&encOpts, "stats", statsFile, 0);
                }
            }
            else
            {
                string crf = opt.CrfOverride > 0
                    ? opt.CrfOverride.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : quality switch
                    {
                        VideoQuality.High => "20",
                        VideoQuality.Low => "28",
                        _ => "23",
                    };
                ffmpeg.av_dict_set(&encOpts, "crf", crf, 0);
            }
            ThrowIfErr(ffmpeg.avcodec_open2(encCtx, enc, &encOpts), "clip_open_encoder");

            outStream = ffmpeg.avformat_new_stream(outFmt, null);
            if (outStream == null)
            {
                throw new OutOfMemoryException("clip_new_stream");
            }
            ThrowIfErr(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encCtx), "clip_stream_par");
            outStream->time_base = encCtx->time_base;
            outStream->avg_frame_rate = encCtx->framerate;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ThrowIfErr(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "clip_avio_open");
            }
            ThrowIfErr(ffmpeg.avformat_write_header(outFmt, null), "clip_write_header");

            inPkt = ffmpeg.av_packet_alloc();
            outPkt = ffmpeg.av_packet_alloc();
            decFrame = ffmpeg.av_frame_alloc();
            encFrame = ffmpeg.av_frame_alloc();
            if (inPkt == null || outPkt == null || decFrame == null || encFrame == null)
            {
                throw new OutOfMemoryException("clip_alloc_packet_frame");
            }

            encFrame->format = (int)encCtx->pix_fmt;
            encFrame->width = encCtx->width;
            encFrame->height = encCtx->height;
            ThrowIfErr(ffmpeg.av_frame_get_buffer(encFrame, 32), "clip_frame_get_buffer");

            if (needBgra)
            {
                // BGRA round-trip: decode(+crop) → BGRA (optional composite, optional rotate, optional
                // post-transform composite) → YUV420P. The decoded frame is cropped IN PLACE before this
                // conversion, so the BGRA stage runs at the post-crop dims (outW×outH = decode dims when
                // not cropping) and the final sws source is the rotated dims when rotating.
                int finalSrcW = rotate ? rotW : outW;
                int finalSrcH = rotate ? rotH : outH;
                swsToBgra = ffmpeg.sws_getContext(
                    outW, outH, decCtx->pix_fmt,
                    outW, outH, AVPixelFormat.AV_PIX_FMT_BGRA,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);
                sws = ffmpeg.sws_getContext(
                    finalSrcW, finalSrcH, AVPixelFormat.AV_PIX_FMT_BGRA,
                    encCtx->width, encCtx->height, encCtx->pix_fmt,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);
                bgraFrame = ffmpeg.av_frame_alloc();
                if (swsToBgra == null || sws == null || bgraFrame == null)
                {
                    throw new InvalidOperationException("Failed to create BGRA composite contexts.");
                }
                bgraFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                bgraFrame->width = outW;
                bgraFrame->height = outH;
                ThrowIfErr(ffmpeg.av_frame_get_buffer(bgraFrame, 32), "clip_bgra_get_buffer");

                if (rotate)
                {
                    rotateDst = new SKBitmap(rotW, rotH, SKColorType.Bgra8888, SKAlphaType.Premul);
                }
            }
            else
            {
                // When cropping, each decoded frame is cropped (av_frame_apply_cropping) down to outW×outH
                // before scaling, so the sws source dimensions are the crop size too.
                sws = ffmpeg.sws_getContext(
                    outW, outH, decCtx->pix_fmt,
                    encCtx->width, encCtx->height, encCtx->pix_fmt,
                    (int)SwsFlags.SWS_BILINEAR, null, null, null);
                if (sws == null)
                {
                    throw new InvalidOperationException("Failed to create sws context for clip export.");
                }
            }

            double tb = ffmpeg.av_q2d(inStream->time_base);

            foreach (SourceRange range in ranges)
            {
                ct.ThrowIfCancellationRequested();

                long seekTs = (long)(range.StartSeconds / tb);
                if (ffmpeg.av_seek_frame(inFmt, videoIn, seekTs, ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0)
                {
                    ffmpeg.avcodec_flush_buffers(decCtx);
                }

                bool runDone = false;
                bool eof = false;
                while (!runDone && !eof)
                {
                    ct.ThrowIfCancellationRequested();
                    // Block here (between frames) while paused; wakes on resume or throws on cancel.
                    pauseGate?.Wait(ct);
                    int rr = ffmpeg.av_read_frame(inFmt, inPkt);
                    if (rr == ffmpeg.AVERROR_EOF)
                    {
                        eof = true;
                        break;
                    }
                    if (rr == ffmpeg.AVERROR(11))
                    {
                        continue;
                    }
                    ThrowIfErr(rr, "clip_read_frame");

                    if (inPkt->stream_index != videoIn)
                    {
                        ffmpeg.av_packet_unref(inPkt);
                        continue;
                    }

                    ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, inPkt), "clip_send_packet");
                    ffmpeg.av_packet_unref(inPkt);

                    runDone = DrainDecodedFrames(
                        decCtx, encCtx, sws, swsToBgra, decFrame, encFrame, bgraFrame, frameComposite,
                        rotation, rotateDst, frameCompositeAfterTransform,
                        outFmt, outStream, outPkt, inStream->time_base, range, crop, ref frameIndex, ref frameAccum,
                        fpsCursorScale, progress, totalSrcDuration, elapsedBeforeRange, ref lastPercent);
                }

                // Range reached the file end: flush the decoder so its buffered tail frames aren't lost.
                if (eof && !runDone)
                {
                    ffmpeg.avcodec_send_packet(decCtx, null);
                    DrainDecodedFrames(
                        decCtx, encCtx, sws, swsToBgra, decFrame, encFrame, bgraFrame, frameComposite,
                        rotation, rotateDst, frameCompositeAfterTransform,
                        outFmt, outStream, outPkt, inStream->time_base, range, crop, ref frameIndex, ref frameAccum,
                        fpsCursorScale, progress, totalSrcDuration, elapsedBeforeRange, ref lastPercent);
                }

                // Reset the decoder before the next range's seek (and after the final flush above).
                ffmpeg.avcodec_flush_buffers(decCtx);
                elapsedBeforeRange += range.Duration;
            }

            // Flush the encoder to drain any buffered output packets.
            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, null), "clip_flush_encoder");
            WriteEncoded(encCtx, outFmt, outStream, outPkt);

            ThrowIfErr(ffmpeg.av_write_trailer(outFmt), "clip_write_trailer");
        }
        finally
        {
            if (inPkt != null) ffmpeg.av_packet_free(&inPkt);
            if (outPkt != null) ffmpeg.av_packet_free(&outPkt);
            if (decFrame != null) ffmpeg.av_frame_free(&decFrame);
            if (encFrame != null) ffmpeg.av_frame_free(&encFrame);
            if (bgraFrame != null) ffmpeg.av_frame_free(&bgraFrame);
            if (sws != null) ffmpeg.sws_freeContext(sws);
            if (swsToBgra != null) ffmpeg.sws_freeContext(swsToBgra);
            rotateDst?.Dispose();
            if (decCtx != null) ffmpeg.avcodec_free_context(&decCtx);
            if (encCtx != null) ffmpeg.avcodec_free_context(&encCtx);
            if (inFmt != null) ffmpeg.avformat_close_input(&inFmt);
            if (outFmt != null)
            {
                if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && outFmt->pb != null)
                {
                    ffmpeg.avio_closep(&outFmt->pb);
                }
                ffmpeg.avformat_free_context(outFmt);
            }
            if (encOpts != null) ffmpeg.av_dict_free(&encOpts);
        }
    }

    // Pulls decoded frames, drops those before the range start, encodes those in [start,end).
    // Returns true once a frame at/after the range end is seen (the run is complete).
    private static unsafe bool DrainDecodedFrames(
        AVCodecContext* decCtx,
        AVCodecContext* encCtx,
        SwsContext* sws,
        SwsContext* swsToBgra,
        AVFrame* decFrame,
        AVFrame* encFrame,
        AVFrame* bgraFrame,
        Action<SKBitmap, double>? composite,
        int rotation,
        SKBitmap? rotateDst,
        Action<SKBitmap, double>? compositeAfterTransform,
        AVFormatContext* outFmt,
        AVStream* outStream,
        AVPacket* outPkt,
        AVRational inTimeBase,
        SourceRange range,
        VideoEditCrop? crop,
        ref long frameIndex,
        ref double frameAccum,
        double fpsCursorScale,
        IProgress<double>? progress,
        double totalSrcDuration,
        double elapsedBeforeRange,
        ref int lastPercent)
    {
        while (true)
        {
            int gr = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
            if (gr == ffmpeg.AVERROR_EOF || gr == ffmpeg.AVERROR(11))
            {
                return false;
            }
            ThrowIfErr(gr, "clip_receive_frame");

            long ts = decFrame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                ? decFrame->best_effort_timestamp
                : decFrame->pts;
            double t = ts != ffmpeg.AV_NOPTS_VALUE ? ts * ffmpeg.av_q2d(inTimeBase) : 0;

            if (t < range.StartSeconds - TimeEpsilon)
            {
                ffmpeg.av_frame_unref(decFrame);
                continue; // before the kept range — skip
            }
            if (t >= range.EndSeconds - TimeEpsilon)
            {
                ffmpeg.av_frame_unref(decFrame);
                return true; // reached the end of this run
            }

            // Crop the decoded frame in place (pointer offset, no copy) to the requested region before
            // scaling. UNALIGNED tolerates odd x/y offsets (chroma is adjusted per plane).
            if (crop is { } c)
            {
                decFrame->crop_left = (ulong)Math.Max(0, c.X);
                decFrame->crop_top = (ulong)Math.Max(0, c.Y);
                decFrame->crop_right = (ulong)Math.Max(0, decCtx->width - c.X - c.Width);
                decFrame->crop_bottom = (ulong)Math.Max(0, decCtx->height - c.Y - c.Height);
                // AV_FRAME_CROP_UNALIGNED = (1 << 0); the named constant isn't exposed by this AutoGen binding.
                ffmpeg.av_frame_apply_cropping(decFrame, 1);
            }

            ThrowIfErr(ffmpeg.av_frame_make_writable(encFrame), "clip_make_writable");
            if (bgraFrame != null && swsToBgra != null)
            {
                // decode pix -> BGRA (our private buffer, full decode dims)
                ffmpeg.sws_scale(swsToBgra, decFrame->data, decFrame->linesize, 0, decFrame->height,
                    bgraFrame->data, bgraFrame->linesize);

                if (composite != null)
                {
                    // Draw annotations/redactions directly onto the BGRA pixels (no copy). t is this frame's
                    // source-time so time-varying composites (e.g. interpolated redaction boxes) can position.
                    // Runs at the post-crop dims (= full source dims when not cropping), before rotation.
                    var info = new SKImageInfo(bgraFrame->width, bgraFrame->height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var sk = new SKBitmap();
                    if (sk.InstallPixels(info, (IntPtr)bgraFrame->data[0], bgraFrame->linesize[0]))
                    {
                        composite(sk, t);
                    }
                }

                if (rotation != 0 && rotateDst != null)
                {
                    // Rotate the BGRA frame into rotateDst (rotated dims) with Skia, then scale BGRA -> YUV.
                    var srcInfo = new SKImageInfo(bgraFrame->width, bgraFrame->height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using (var src = new SKBitmap())
                    {
                        if (src.InstallPixels(srcInfo, (IntPtr)bgraFrame->data[0], bgraFrame->linesize[0]))
                        {
                            using var canvas = new SKCanvas(rotateDst);
                            canvas.Clear();
                            canvas.Translate(rotateDst.Width / 2f, rotateDst.Height / 2f);
                            canvas.RotateDegrees(rotation);
                            canvas.Translate(-src.Width / 2f, -src.Height / 2f);
                            canvas.DrawBitmap(src, 0, 0);
                            canvas.Flush();
                        }
                    }

                    // Post-transform composite: drawn on the CROPPED+ROTATED frame (what the editor preview
                    // shows), so surface-space annotations land exactly where the user drew them.
                    compositeAfterTransform?.Invoke(rotateDst, t);

                    byte*[] rotData = { (byte*)rotateDst.GetPixels().ToPointer(), null, null, null };
                    int[] rotLine = { rotateDst.RowBytes, 0, 0, 0 };
                    ffmpeg.sws_scale(sws, rotData, rotLine, 0, rotateDst.Height, encFrame->data, encFrame->linesize);
                }
                else
                {
                    if (compositeAfterTransform != null)
                    {
                        // No rotation: the post-transform frame IS the (possibly cropped) BGRA frame.
                        var postInfo = new SKImageInfo(bgraFrame->width, bgraFrame->height, SKColorType.Bgra8888, SKAlphaType.Premul);
                        using var skPost = new SKBitmap();
                        if (skPost.InstallPixels(postInfo, (IntPtr)bgraFrame->data[0], bgraFrame->linesize[0]))
                        {
                            compositeAfterTransform(skPost, t);
                        }
                    }

                    ffmpeg.sws_scale(sws, bgraFrame->data, bgraFrame->linesize, 0, bgraFrame->height,
                        encFrame->data, encFrame->linesize);
                }
            }
            else
            {
                ffmpeg.sws_scale(sws, decFrame->data, decFrame->linesize, 0, decFrame->height,
                    encFrame->data, encFrame->linesize);
            }
            // Report encode progress by source-time consumed (this frame's offset within the kept ranges),
            // throttled to whole-percent changes.
            if (progress != null)
            {
                double done = elapsedBeforeRange + Math.Clamp(t - range.StartSeconds, 0, range.Duration);
                int pct = (int)(done / totalSrcDuration * 100.0);
                if (pct != lastPercent)
                {
                    lastPercent = pct;
                    progress.Report(Math.Clamp(done / totalSrcDuration, 0, 1));
                }
            }

            // Per-segment speed AND fps cap fold into one cursor advance: each source frame spans
            // 1/(speed × fpsScale) output frames. fpsScale = srcFps/outFps decimates to the capped rate
            // (≥1 drops frames); both are 1.0 in the default path, so behaviour there is unchanged.
            int emitCount = AdvanceFrameCursor(ref frameAccum, range.EffectiveSpeed * fpsCursorScale);

            for (int e = 0; e < emitCount; e++)
            {
                encFrame->pts = frameIndex++;
                ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, encFrame), "clip_send_frame");
                WriteEncoded(encCtx, outFmt, outStream, outPkt);
            }

            ffmpeg.av_frame_unref(decFrame);
        }
    }

    private static unsafe void WriteEncoded(
        AVCodecContext* encCtx, AVFormatContext* outFmt, AVStream* outStream, AVPacket* outPkt)
    {
        while (true)
        {
            int gp = ffmpeg.avcodec_receive_packet(encCtx, outPkt);
            if (gp == ffmpeg.AVERROR_EOF || gp == ffmpeg.AVERROR(11))
            {
                break;
            }
            ThrowIfErr(gp, "clip_receive_packet");

            ffmpeg.av_packet_rescale_ts(outPkt, encCtx->time_base, outStream->time_base);
            outPkt->stream_index = outStream->index;
            ThrowIfErr(ffmpeg.av_interleaved_write_frame(outFmt, outPkt), "clip_write_packet");
            ffmpeg.av_packet_unref(outPkt);
        }
    }

    /// <summary>Probes the source's video frame rate (rounded, clamped). Falls back to 30 on any failure.</summary>
    public static int ProbeFps(string inputPath)
    {
        FFmpegRuntime.EnsureInitialized();
        return ProbeFpsUnsafe(inputPath);
    }

    private static unsafe int ProbeFpsUnsafe(string inputPath)
    {
        AVFormatContext* fmt = null;
        try
        {
            if (ffmpeg.avformat_open_input(&fmt, inputPath, null, null) < 0) return 30;
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0) return 30;
            int v = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            return v < 0 ? 30 : ResolveFps(fmt->streams[v]);
        }
        catch
        {
            return 30;
        }
        finally
        {
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }

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

    // ── Audio: decode each range to PCM (truncated to range length), concat, encode AAC ──
    private static bool TryBuildAudio(
        string inputPath,
        IReadOnlyList<SourceRange> ranges,
        string tempDir,
        VideoQuality quality,
        LibavExportOptions opt,
        CancellationToken ct,
        out string? audioTemp)
    {
        audioTemp = null;

        try
        {
            WaveFormat? format = null;
            using var pcm = new MemoryStream();

            foreach (SourceRange range in ranges)
            {
                ct.ThrowIfCancellationRequested();
                LibavPinAudioPcmDecoder.DecodeResult decoded =
                    LibavPinAudioPcmDecoder.Decode(inputPath, range.StartSeconds, ct);
                if (decoded.PcmBytes.Length == 0)
                {
                    continue;
                }

                format ??= decoded.WaveFormat;
                int bytesPerSecond = decoded.WaveFormat.SampleRate * decoded.WaveFormat.Channels * 2; // S16
                long keep = (long)Math.Round(range.Duration * bytesPerSecond);
                keep -= keep % (decoded.WaveFormat.Channels * 2); // align to a full sample frame
                keep = Math.Clamp(keep, 0, decoded.PcmBytes.Length);

                if (Math.Abs(range.EffectiveSpeed - 1.0) > 0.001)
                {
                    // Pitch-preserving retime so the audio matches this segment's video speed.
                    byte[] slice = new byte[keep];
                    Array.Copy(decoded.PcmBytes, slice, (int)keep);
                    byte[] retimed = LibavAtempoFilter.Process(
                        slice, decoded.WaveFormat.SampleRate, decoded.WaveFormat.Channels, range.EffectiveSpeed);
                    AppLog.Information(
                        $"LibavClipExporter.AudioRetime speed={range.EffectiveSpeed:0.###} in={slice.Length} out={retimed.Length}");
                    pcm.Write(retimed, 0, retimed.Length);
                }
                else
                {
                    pcm.Write(decoded.PcmBytes, 0, (int)keep);
                }
            }

            if (format == null || pcm.Length == 0)
            {
                AppLog.Information($"LibavClipExporter.TryBuildAudio no-audio (format={(format == null ? "null" : "set")} pcmBytes={pcm.Length})");
                return false; // no audio stream, or nothing decoded
            }

            string wavTemp = Path.Combine(tempDir, "audio.wav");
            byte[] pcmBytes = pcm.ToArray();
            using (var writer = new WaveFileWriter(wavTemp, format))
            {
                writer.Write(pcmBytes, 0, pcmBytes.Length);
            }

            audioTemp = Path.Combine(tempDir, "audio.m4a");
            LibavAacTranscoder.EncodeWavToM4a(wavTemp, audioTemp, quality, opt.AudioBitrateKbps, opt.AudioChannels);
            bool ok = File.Exists(audioTemp) && new FileInfo(audioTemp).Length > 0;
            AppLog.Information($"LibavClipExporter.TryBuildAudio built={ok} pcmBytes={pcmBytes.Length}");
            return ok;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No usable audio (e.g. silent recording) — export video only rather than failing. Log so a
            // genuine audio-pipeline failure isn't silently swallowed into a soundless export.
            AppLog.Warning("LibavClipExporter.TryBuildAudio", ex);
            audioTemp = null;
            return false;
        }
    }

    private static void ThrowIfErr(int err, string ctx)
    {
        if (err < 0)
        {
            throw FFmpegErrors.ToException(err, ctx);
        }
    }

    /// <summary>Maps an inner 0..1 progress fraction onto [offset, offset+scale] (for multi-pass encodes).</summary>
    private sealed class ScaledProgress : IProgress<double>
    {
        private readonly IProgress<double> _inner;
        private readonly double _offset;
        private readonly double _scale;

        public ScaledProgress(IProgress<double> inner, double offset, double scale)
        {
            _inner = inner;
            _offset = offset;
            _scale = scale;
        }

        public void Report(double value) => _inner.Report(_offset + (_scale * value));
    }
}
