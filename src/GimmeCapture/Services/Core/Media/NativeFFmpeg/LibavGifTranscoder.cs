using System;
using FFmpeg.AutoGen;

using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

internal static class LibavGifTranscoder
{
    /// <summary>
    /// Quality → (fps, max output width; 0 = source width) ladder, the single home for the numbers that
    /// were previously duplicated by the recording finalize and the pinned-video GIF export.
    /// </summary>
    public static (int Fps, int MaxWidth) QualityLadder(VideoQuality quality) => quality switch
    {
        VideoQuality.High => (24, 0),
        VideoQuality.Low => (10, 480),
        _ => (15, 720),
    };

    /// <summary>
    /// Transcodes to GIF. Tries a two-pass optimal-palette filtergraph (palettegen → paletteuse) for
    /// much better color, and falls back to the single-pass RGB8 path if the filtergraph is unavailable
    /// or fails — so GIF export never regresses.
    /// </summary>
    public static unsafe void TranscodeToGif(
        string inputPath,
        string outputPath,
        int targetFps,
        int maxWidth,
        string paletteuseArgs = "dither=sierra2_4a")
    {
        FFmpegRuntime.EnsureInitialized();
        try
        {
            TranscodeToGifWithPalette(inputPath, outputPath, targetFps, maxWidth, paletteuseArgs);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Gif] Optimal-palette transcode failed ({ex.Message}); falling back to single-pass RGB8.");
            TranscodeToGifRgb8(inputPath, outputPath, targetFps, maxWidth);
        }
    }

    private static unsafe void TranscodeToGifRgb8(
        string inputPath,
        string outputPath,
        int targetFps,
        int maxWidth)
    {
        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* inFmt = null;
        AVCodecContext* decCtx = null;
        AVFormatContext* outFmt = null;
        AVCodecContext* encCtx = null;
        SwsContext* sws = null;
        AVPacket* inPkt = null;
        AVPacket* outPkt = null;
        AVFrame* decFrame = null;
        AVFrame* encFrame = null;
        int videoIn = -1;
        AVStream* outStream = null;

        try
        {
            ThrowIfErr(ffmpeg.avformat_open_input(&inFmt, inputPath, null, null), "gif_open_input");
            ThrowIfErr(ffmpeg.avformat_find_stream_info(inFmt, null), "gif_find_stream_info");
            videoIn = ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoIn < 0)
            {
                throw new InvalidOperationException("No video stream for GIF transcoding.");
            }

            AVStream* inStream = inFmt->streams[videoIn];
            AVCodec* dec = ffmpeg.avcodec_find_decoder(inStream->codecpar->codec_id);
            if (dec == null)
            {
                throw new InvalidOperationException("Video decoder unavailable for GIF transcoding.");
            }

            decCtx = ffmpeg.avcodec_alloc_context3(dec);
            ThrowIfErr(ffmpeg.avcodec_parameters_to_context(decCtx, inStream->codecpar), "gif_par_to_ctx");
            ThrowIfErr(ffmpeg.avcodec_open2(decCtx, dec, null), "gif_open_decoder");

            int srcW = decCtx->width > 0 ? decCtx->width : inStream->codecpar->width;
            int srcH = decCtx->height > 0 ? decCtx->height : inStream->codecpar->height;
            if (srcW <= 0 || srcH <= 0)
            {
                throw new InvalidOperationException("Invalid input dimensions for GIF transcoding.");
            }

            int outW = srcW;
            int outH = srcH;
            if (maxWidth > 0 && srcW > maxWidth)
            {
                double scale = maxWidth / (double)srcW;
                outW = maxWidth;
                outH = Math.Max(1, (int)Math.Round(srcH * scale));
            }

            int fps = Math.Clamp(targetFps, 1, 60);
            AVRational guessedFrameRate = ffmpeg.av_guess_frame_rate(inFmt, inStream, null);
            double sourceFps = guessedFrameRate.num > 0 && guessedFrameRate.den > 0
                ? ffmpeg.av_q2d(guessedFrameRate)
                : fps;
            var frameTimeline = new GifFrameTimeline(
                ffmpeg.av_q2d(inStream->time_base),
                fps,
                sourceFps);

            ThrowIfErr(ffmpeg.avformat_alloc_output_context2(&outFmt, null, "gif", outputPath), "gif_alloc_output");
            if (outFmt == null)
            {
                throw new InvalidOperationException("Failed to allocate GIF output context.");
            }

            AVCodec* enc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_GIF);
            if (enc == null)
            {
                throw new InvalidOperationException("GIF encoder unavailable.");
            }

            encCtx = ffmpeg.avcodec_alloc_context3(enc);
            encCtx->codec_id = AVCodecID.AV_CODEC_ID_GIF;
            encCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            encCtx->width = outW;
            encCtx->height = outH;
            encCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_RGB8;
            encCtx->time_base = new AVRational { num = 1, den = fps };
            encCtx->framerate = new AVRational { num = fps, den = 1 };

            ThrowIfErr(ffmpeg.avcodec_open2(encCtx, enc, null), "gif_open_encoder");

            outStream = ffmpeg.avformat_new_stream(outFmt, null);
            if (outStream == null)
            {
                throw new OutOfMemoryException("gif_new_stream");
            }

            ThrowIfErr(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encCtx), "gif_stream_par");
            outStream->time_base = encCtx->time_base;
            outStream->avg_frame_rate = encCtx->framerate;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ThrowIfErr(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "gif_avio_open");
            }

            ThrowIfErr(ffmpeg.avformat_write_header(outFmt, null), "gif_write_header");

            inPkt = ffmpeg.av_packet_alloc();
            outPkt = ffmpeg.av_packet_alloc();
            decFrame = ffmpeg.av_frame_alloc();
            encFrame = ffmpeg.av_frame_alloc();
            if (inPkt == null || outPkt == null || decFrame == null || encFrame == null)
            {
                throw new OutOfMemoryException("gif_alloc_packet_frame");
            }

            encFrame->format = (int)encCtx->pix_fmt;
            encFrame->width = encCtx->width;
            encFrame->height = encCtx->height;
            ThrowIfErr(ffmpeg.av_frame_get_buffer(encFrame, 32), "gif_frame_get_buffer");

            sws = ffmpeg.sws_getContext(
                srcW, srcH, decCtx->pix_fmt,
                outW, outH, encCtx->pix_fmt,
                (int)SwsFlags.SWS_BILINEAR,
                null, null, null);
            if (sws == null)
            {
                throw new InvalidOperationException("Failed to create sws context for GIF.");
            }

            while (true)
            {
                int rr = ffmpeg.av_read_frame(inFmt, inPkt);
                if (rr == ffmpeg.AVERROR_EOF)
                {
                    break;
                }
                if (rr == ffmpeg.AVERROR(11))
                {
                    continue;
                }
                ThrowIfErr(rr, "gif_read_frame");

                if (inPkt->stream_index != videoIn)
                {
                    ffmpeg.av_packet_unref(inPkt);
                    continue;
                }

                ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, inPkt), "gif_send_packet");
                ffmpeg.av_packet_unref(inPkt);

                ReceiveAndEncode(
                    decCtx,
                    encCtx,
                    sws,
                    decFrame,
                    encFrame,
                    outFmt,
                    outStream,
                    outPkt,
                    frameTimeline);
            }

            ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, null), "gif_flush_decoder");
            ReceiveAndEncode(
                decCtx,
                encCtx,
                sws,
                decFrame,
                encFrame,
                outFmt,
                outStream,
                outPkt,
                frameTimeline);

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, null), "gif_flush_encoder");
            WriteEncoded(encCtx, outFmt, outStream, outPkt);

            ThrowIfErr(ffmpeg.av_write_trailer(outFmt), "gif_write_trailer");
        }
        finally
        {
            if (inPkt != null) ffmpeg.av_packet_free(&inPkt);
            if (outPkt != null) ffmpeg.av_packet_free(&outPkt);
            if (decFrame != null) ffmpeg.av_frame_free(&decFrame);
            if (encFrame != null) ffmpeg.av_frame_free(&encFrame);
            if (sws != null) ffmpeg.sws_freeContext(sws);
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
        }
    }

    // Two-pass optimal palette via a fused buffer → split → palettegen/paletteuse → buffersink graph
    // (the same approach as ffmpeg's `palettegen,paletteuse` GIF recipe), giving far better color than the
    // fixed RGB8 332-palette. Frames are fed as RGB24; the graph buffers them until palettegen emits its
    // optimal palette at EOF, then paletteuse maps every frame to PAL8 for the GIF encoder.
    private static unsafe void TranscodeToGifWithPalette(
        string inputPath,
        string outputPath,
        int targetFps,
        int maxWidth,
        string paletteuseArgs)
    {
        AVFormatContext* inFmt = null;
        AVCodecContext* decCtx = null;
        AVFormatContext* outFmt = null;
        AVCodecContext* encCtx = null;
        SwsContext* sws = null;
        AVFilterGraph* graph = null;
        AVPacket* inPkt = null;
        AVPacket* outPkt = null;
        AVFrame* decFrame = null;
        AVFrame* rgbFrame = null;
        AVFrame* palFrame = null;
        AVStream* outStream = null;
        int videoIn = -1;

        try
        {
            ThrowIfErr(ffmpeg.avformat_open_input(&inFmt, inputPath, null, null), "gifp_open_input");
            ThrowIfErr(ffmpeg.avformat_find_stream_info(inFmt, null), "gifp_find_stream_info");
            videoIn = ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoIn < 0)
            {
                throw new InvalidOperationException("No video stream for GIF transcoding.");
            }

            AVStream* inStream = inFmt->streams[videoIn];
            AVCodec* dec = ffmpeg.avcodec_find_decoder(inStream->codecpar->codec_id);
            if (dec == null)
            {
                throw new InvalidOperationException("Video decoder unavailable for GIF transcoding.");
            }

            decCtx = ffmpeg.avcodec_alloc_context3(dec);
            ThrowIfErr(ffmpeg.avcodec_parameters_to_context(decCtx, inStream->codecpar), "gifp_par_to_ctx");
            ThrowIfErr(ffmpeg.avcodec_open2(decCtx, dec, null), "gifp_open_decoder");

            int srcW = decCtx->width > 0 ? decCtx->width : inStream->codecpar->width;
            int srcH = decCtx->height > 0 ? decCtx->height : inStream->codecpar->height;
            if (srcW <= 0 || srcH <= 0)
            {
                throw new InvalidOperationException("Invalid input dimensions for GIF transcoding.");
            }

            int outW = srcW;
            int outH = srcH;
            if (maxWidth > 0 && srcW > maxWidth)
            {
                double scale = maxWidth / (double)srcW;
                outW = maxWidth;
                outH = Math.Max(1, (int)Math.Round(srcH * scale));
            }
            outW = Math.Max(2, outW & ~1);
            outH = Math.Max(2, outH & ~1);

            int fps = Math.Clamp(targetFps, 1, 60);
            AVRational guessed = ffmpeg.av_guess_frame_rate(inFmt, inStream, null);
            double sourceFps = guessed.num > 0 && guessed.den > 0 ? ffmpeg.av_q2d(guessed) : fps;
            var timeline = new GifFrameTimeline(ffmpeg.av_q2d(inStream->time_base), fps, sourceFps);

            sws = ffmpeg.sws_getContext(
                srcW, srcH, decCtx->pix_fmt,
                outW, outH, AVPixelFormat.AV_PIX_FMT_RGB24,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (sws == null)
            {
                throw new InvalidOperationException("Failed to create sws context for GIF palette.");
            }

            graph = ffmpeg.avfilter_graph_alloc();
            if (graph == null)
            {
                throw new OutOfMemoryException("gifp_graph_alloc");
            }

            AVFilter* bufferF = ffmpeg.avfilter_get_by_name("buffer");
            AVFilter* splitF = ffmpeg.avfilter_get_by_name("split");
            AVFilter* palettegenF = ffmpeg.avfilter_get_by_name("palettegen");
            AVFilter* paletteuseF = ffmpeg.avfilter_get_by_name("paletteuse");
            AVFilter* sinkF = ffmpeg.avfilter_get_by_name("buffersink");
            if (bufferF == null || splitF == null || palettegenF == null || paletteuseF == null || sinkF == null)
            {
                throw new InvalidOperationException("palettegen/paletteuse filters unavailable in this libav build.");
            }

            string srcArgs = $"video_size={outW}x{outH}:pix_fmt={(int)AVPixelFormat.AV_PIX_FMT_RGB24}:time_base=1/{fps}:pixel_aspect=1/1";
            AVFilterContext* srcCtx = null;
            AVFilterContext* splitCtx = null;
            AVFilterContext* pgCtx = null;
            AVFilterContext* puCtx = null;
            AVFilterContext* sinkCtx = null;
            ThrowIfErr(ffmpeg.avfilter_graph_create_filter(&srcCtx, bufferF, "in", srcArgs, null, graph), "gifp_src");
            ThrowIfErr(ffmpeg.avfilter_graph_create_filter(&splitCtx, splitF, "split", null, null, graph), "gifp_split");
            ThrowIfErr(ffmpeg.avfilter_graph_create_filter(&pgCtx, palettegenF, "pg", "stats_mode=full", null, graph), "gifp_pg");
            ThrowIfErr(ffmpeg.avfilter_graph_create_filter(&puCtx, paletteuseF, "pu", paletteuseArgs, null, graph), "gifp_pu");
            ThrowIfErr(ffmpeg.avfilter_graph_create_filter(&sinkCtx, sinkF, "out", null, null, graph), "gifp_sink");

            ThrowIfErr(ffmpeg.avfilter_link(srcCtx, 0, splitCtx, 0), "gifp_link_src_split");
            ThrowIfErr(ffmpeg.avfilter_link(splitCtx, 0, puCtx, 0), "gifp_link_split_pu"); // video branch -> paletteuse in0
            ThrowIfErr(ffmpeg.avfilter_link(splitCtx, 1, pgCtx, 0), "gifp_link_split_pg"); // palette branch -> palettegen
            ThrowIfErr(ffmpeg.avfilter_link(pgCtx, 0, puCtx, 1), "gifp_link_pg_pu");       // palette -> paletteuse in1
            ThrowIfErr(ffmpeg.avfilter_link(puCtx, 0, sinkCtx, 0), "gifp_link_pu_sink");
            ThrowIfErr(ffmpeg.avfilter_graph_config(graph, null), "gifp_graph_config");

            ThrowIfErr(ffmpeg.avformat_alloc_output_context2(&outFmt, null, "gif", outputPath), "gifp_alloc_output");
            if (outFmt == null)
            {
                throw new InvalidOperationException("Failed to allocate GIF output context.");
            }

            AVCodec* enc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_GIF);
            if (enc == null)
            {
                throw new InvalidOperationException("GIF encoder unavailable.");
            }

            encCtx = ffmpeg.avcodec_alloc_context3(enc);
            encCtx->codec_id = AVCodecID.AV_CODEC_ID_GIF;
            encCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            encCtx->width = outW;
            encCtx->height = outH;
            encCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_PAL8;
            encCtx->time_base = new AVRational { num = 1, den = fps };
            encCtx->framerate = new AVRational { num = fps, den = 1 };
            ThrowIfErr(ffmpeg.avcodec_open2(encCtx, enc, null), "gifp_open_encoder");

            outStream = ffmpeg.avformat_new_stream(outFmt, null);
            if (outStream == null)
            {
                throw new OutOfMemoryException("gifp_new_stream");
            }

            ThrowIfErr(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encCtx), "gifp_stream_par");
            outStream->time_base = encCtx->time_base;
            outStream->avg_frame_rate = encCtx->framerate;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ThrowIfErr(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "gifp_avio_open");
            }

            ThrowIfErr(ffmpeg.avformat_write_header(outFmt, null), "gifp_write_header");

            inPkt = ffmpeg.av_packet_alloc();
            outPkt = ffmpeg.av_packet_alloc();
            decFrame = ffmpeg.av_frame_alloc();
            rgbFrame = ffmpeg.av_frame_alloc();
            palFrame = ffmpeg.av_frame_alloc();

            while (true)
            {
                int rr = ffmpeg.av_read_frame(inFmt, inPkt);
                if (rr == ffmpeg.AVERROR_EOF)
                {
                    break;
                }
                if (rr == ffmpeg.AVERROR(11))
                {
                    continue;
                }
                ThrowIfErr(rr, "gifp_read_frame");

                if (inPkt->stream_index != videoIn)
                {
                    ffmpeg.av_packet_unref(inPkt);
                    continue;
                }

                ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, inPkt), "gifp_send_packet");
                ffmpeg.av_packet_unref(inPkt);
                FeedDecodedFramesToGraph(decCtx, sws, decFrame, rgbFrame, srcCtx, outW, outH, timeline);
            }

            ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, null), "gifp_flush_decoder");
            FeedDecodedFramesToGraph(decCtx, sws, decFrame, rgbFrame, srcCtx, outW, outH, timeline);
            ThrowIfErr(ffmpeg.av_buffersrc_add_frame(srcCtx, null), "gifp_src_eof"); // palettegen emits palette at EOF

            while (true)
            {
                int gf = ffmpeg.av_buffersink_get_frame(sinkCtx, palFrame);
                if (gf == ffmpeg.AVERROR_EOF || gf == ffmpeg.AVERROR(11))
                {
                    break;
                }
                ThrowIfErr(gf, "gifp_sink_pull");
                ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, palFrame), "gifp_enc_send");
                ffmpeg.av_frame_unref(palFrame);
                WriteEncoded(encCtx, outFmt, outStream, outPkt);
            }

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, null), "gifp_enc_flush");
            WriteEncoded(encCtx, outFmt, outStream, outPkt);
            ThrowIfErr(ffmpeg.av_write_trailer(outFmt), "gifp_write_trailer");
        }
        finally
        {
            if (inPkt != null) ffmpeg.av_packet_free(&inPkt);
            if (outPkt != null) ffmpeg.av_packet_free(&outPkt);
            if (decFrame != null) ffmpeg.av_frame_free(&decFrame);
            if (rgbFrame != null) ffmpeg.av_frame_free(&rgbFrame);
            if (palFrame != null) ffmpeg.av_frame_free(&palFrame);
            if (sws != null) ffmpeg.sws_freeContext(sws);
            if (graph != null) ffmpeg.avfilter_graph_free(&graph);
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
        }
    }

    private static unsafe void FeedDecodedFramesToGraph(
        AVCodecContext* decCtx,
        SwsContext* sws,
        AVFrame* decFrame,
        AVFrame* rgbFrame,
        AVFilterContext* srcCtx,
        int outW,
        int outH,
        GifFrameTimeline timeline)
    {
        while (true)
        {
            int gr = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
            if (gr == ffmpeg.AVERROR_EOF || gr == ffmpeg.AVERROR(11))
            {
                break;
            }
            ThrowIfErr(gr, "gifp_receive_frame");

            long? ts = decFrame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                ? decFrame->best_effort_timestamp
                : null;
            if (!timeline.TrySchedule(ts, out long outputPts))
            {
                continue;
            }

            ffmpeg.av_frame_unref(rgbFrame);
            rgbFrame->format = (int)AVPixelFormat.AV_PIX_FMT_RGB24;
            rgbFrame->width = outW;
            rgbFrame->height = outH;
            ThrowIfErr(ffmpeg.av_frame_get_buffer(rgbFrame, 0), "gifp_rgb_buffer");
            ffmpeg.sws_scale(sws, decFrame->data, decFrame->linesize, 0, decFrame->height, rgbFrame->data, rgbFrame->linesize);
            rgbFrame->pts = outputPts;
            ThrowIfErr(ffmpeg.av_buffersrc_add_frame(srcCtx, rgbFrame), "gifp_src_push"); // consumes rgbFrame
        }
    }

    private static unsafe void ReceiveAndEncode(
        AVCodecContext* decCtx,
        AVCodecContext* encCtx,
        SwsContext* sws,
        AVFrame* decFrame,
        AVFrame* encFrame,
        AVFormatContext* outFmt,
        AVStream* outStream,
        AVPacket* outPkt,
        GifFrameTimeline frameTimeline)
    {
        while (true)
        {
            int gr = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
            if (gr == ffmpeg.AVERROR_EOF || gr == ffmpeg.AVERROR(11))
            {
                break;
            }
            ThrowIfErr(gr, "gif_receive_frame");

            long? inputTimestamp = decFrame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
                ? decFrame->best_effort_timestamp
                : null;
            if (!frameTimeline.TrySchedule(inputTimestamp, out long outputPts))
            {
                continue;
            }

            ThrowIfErr(ffmpeg.av_frame_make_writable(encFrame), "gif_make_writable");
            ffmpeg.sws_scale(
                sws,
                decFrame->data,
                decFrame->linesize,
                0,
                decFrame->height,
                encFrame->data,
                encFrame->linesize);
            encFrame->pts = outputPts;

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, encFrame), "gif_send_frame");
            WriteEncoded(encCtx, outFmt, outStream, outPkt);
        }
    }

    private static unsafe void WriteEncoded(
        AVCodecContext* encCtx,
        AVFormatContext* outFmt,
        AVStream* outStream,
        AVPacket* outPkt)
    {
        while (true)
        {
            int gp = ffmpeg.avcodec_receive_packet(encCtx, outPkt);
            if (gp == ffmpeg.AVERROR_EOF || gp == ffmpeg.AVERROR(11))
            {
                break;
            }
            ThrowIfErr(gp, "gif_receive_packet");

            ffmpeg.av_packet_rescale_ts(outPkt, encCtx->time_base, outStream->time_base);
            outPkt->stream_index = outStream->index;
            ThrowIfErr(ffmpeg.av_interleaved_write_frame(outFmt, outPkt), "gif_write_packet");
            ffmpeg.av_packet_unref(outPkt);
        }
    }

    private static void ThrowIfErr(int err, string ctx)
    {
        if (err < 0)
        {
            throw FFmpegErrors.ToException(err, ctx);
        }
    }
}

internal sealed class GifFrameTimeline
{
    private readonly double _inputTimeBaseSeconds;
    private readonly int _targetFps;
    private readonly double _fallbackSourceFps;
    private long? _firstInputTimestamp;
    private long _decodedFrameIndex;
    private long _lastOutputPts = -1;

    public GifFrameTimeline(double inputTimeBaseSeconds, int targetFps, double fallbackSourceFps)
    {
        _inputTimeBaseSeconds = inputTimeBaseSeconds > 0 ? inputTimeBaseSeconds : 0;
        _targetFps = Math.Clamp(targetFps, 1, 60);
        _fallbackSourceFps = fallbackSourceFps > 0 ? fallbackSourceFps : _targetFps;
    }

    public bool TrySchedule(long? inputTimestamp, out long outputPts)
    {
        double elapsedSeconds;
        if (inputTimestamp.HasValue && _inputTimeBaseSeconds > 0)
        {
            _firstInputTimestamp ??= inputTimestamp.Value;
            elapsedSeconds = Math.Max(
                0,
                (inputTimestamp.Value - _firstInputTimestamp.Value) * _inputTimeBaseSeconds);
        }
        else
        {
            elapsedSeconds = _decodedFrameIndex / _fallbackSourceFps;
        }

        _decodedFrameIndex++;
        long candidatePts = Math.Max(
            0,
            (long)Math.Floor((elapsedSeconds * _targetFps) + 0.000001));
        if (candidatePts <= _lastOutputPts)
        {
            outputPts = 0;
            return false;
        }

        _lastOutputPts = candidatePts;
        outputPts = candidatePts;
        return true;
    }
}
