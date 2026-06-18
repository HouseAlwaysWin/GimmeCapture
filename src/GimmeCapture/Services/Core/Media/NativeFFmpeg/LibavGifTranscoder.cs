using System;
using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

internal static class LibavGifTranscoder
{
    public static unsafe void TranscodeToGif(
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
