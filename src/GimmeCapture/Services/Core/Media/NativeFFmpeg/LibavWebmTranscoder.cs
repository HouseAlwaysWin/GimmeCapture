using System;
using FFmpeg.AutoGen;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

internal static class LibavWebmTranscoder
{
    public static unsafe void TranscodeToWebm(
        string inputPath,
        string outputPath,
        int targetFps,
        VideoQuality quality)
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
        AVDictionary* encOpts = null;
        int videoIn = -1;
        AVStream* outStream = null;
        long frameIndex = 0;

        try
        {
            ThrowIfErr(ffmpeg.avformat_open_input(&inFmt, inputPath, null, null), "webm_open_input");
            ThrowIfErr(ffmpeg.avformat_find_stream_info(inFmt, null), "webm_find_stream_info");
            videoIn = ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoIn < 0)
            {
                throw new InvalidOperationException("No video stream for WebM transcoding.");
            }

            AVStream* inStream = inFmt->streams[videoIn];
            AVCodec* dec = ffmpeg.avcodec_find_decoder(inStream->codecpar->codec_id);
            if (dec == null)
            {
                throw new InvalidOperationException("Video decoder unavailable for WebM transcoding.");
            }

            decCtx = ffmpeg.avcodec_alloc_context3(dec);
            ThrowIfErr(ffmpeg.avcodec_parameters_to_context(decCtx, inStream->codecpar), "webm_par_to_ctx");
            ThrowIfErr(ffmpeg.avcodec_open2(decCtx, dec, null), "webm_open_decoder");

            AVCodec* enc = ffmpeg.avcodec_find_encoder_by_name("libvpx-vp9");
            if (enc == null)
            {
                throw new InvalidOperationException("VP9 encoder (libvpx-vp9) unavailable.");
            }

            int fps = Math.Clamp(targetFps, 1, 60);

            encCtx = ffmpeg.avcodec_alloc_context3(enc);
            encCtx->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            encCtx->codec_id = enc->id;
            encCtx->width = decCtx->width;
            encCtx->height = decCtx->height;
            encCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            encCtx->time_base = new AVRational { num = 1, den = fps };
            encCtx->framerate = new AVRational { num = fps, den = 1 };

            var (crf, cpuUsed) = quality switch
            {
                VideoQuality.High => ("18", "1"),
                VideoQuality.Low => ("35", "4"),
                _ => ("25", "2")
            };
            ffmpeg.av_dict_set(&encOpts, "crf", crf, 0);
            ffmpeg.av_dict_set(&encOpts, "b", "0", 0);
            ffmpeg.av_dict_set(&encOpts, "cpu-used", cpuUsed, 0);
            ThrowIfErr(ffmpeg.avcodec_open2(encCtx, enc, &encOpts), "webm_open_encoder");

            ThrowIfErr(ffmpeg.avformat_alloc_output_context2(&outFmt, null, "webm", outputPath), "webm_alloc_output");
            if (outFmt == null)
            {
                throw new InvalidOperationException("Failed to allocate WebM output context.");
            }

            outStream = ffmpeg.avformat_new_stream(outFmt, null);
            if (outStream == null)
            {
                throw new OutOfMemoryException("webm_new_stream");
            }

            ThrowIfErr(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encCtx), "webm_stream_par");
            outStream->time_base = encCtx->time_base;
            outStream->avg_frame_rate = encCtx->framerate;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ThrowIfErr(ffmpeg.avio_open(&outFmt->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "webm_avio_open");
            }

            ThrowIfErr(ffmpeg.avformat_write_header(outFmt, null), "webm_write_header");

            inPkt = ffmpeg.av_packet_alloc();
            outPkt = ffmpeg.av_packet_alloc();
            decFrame = ffmpeg.av_frame_alloc();
            encFrame = ffmpeg.av_frame_alloc();
            if (inPkt == null || outPkt == null || decFrame == null || encFrame == null)
            {
                throw new OutOfMemoryException("webm_alloc_packet_frame");
            }

            encFrame->format = (int)encCtx->pix_fmt;
            encFrame->width = encCtx->width;
            encFrame->height = encCtx->height;
            ThrowIfErr(ffmpeg.av_frame_get_buffer(encFrame, 32), "webm_frame_get_buffer");

            sws = ffmpeg.sws_getContext(
                decCtx->width, decCtx->height, decCtx->pix_fmt,
                encCtx->width, encCtx->height, encCtx->pix_fmt,
                (int)SwsFlags.SWS_BILINEAR,
                null, null, null);
            if (sws == null)
            {
                throw new InvalidOperationException("Failed to create sws context for WebM.");
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
                ThrowIfErr(rr, "webm_read_frame");

                if (inPkt->stream_index != videoIn)
                {
                    ffmpeg.av_packet_unref(inPkt);
                    continue;
                }

                ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, inPkt), "webm_send_packet");
                ffmpeg.av_packet_unref(inPkt);

                ReceiveAndEncode(decCtx, encCtx, sws, decFrame, encFrame, outFmt, outStream, outPkt, ref frameIndex);
            }

            ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, null), "webm_flush_decoder");
            ReceiveAndEncode(decCtx, encCtx, sws, decFrame, encFrame, outFmt, outStream, outPkt, ref frameIndex);

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, null), "webm_flush_encoder");
            WriteEncoded(encCtx, outFmt, outStream, outPkt);

            ThrowIfErr(ffmpeg.av_write_trailer(outFmt), "webm_write_trailer");
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
            if (encOpts != null)
            {
                ffmpeg.av_dict_free(&encOpts);
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
        ref long frameIndex)
    {
        while (true)
        {
            int gr = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
            if (gr == ffmpeg.AVERROR_EOF || gr == ffmpeg.AVERROR(11))
            {
                break;
            }
            ThrowIfErr(gr, "webm_receive_frame");

            ThrowIfErr(ffmpeg.av_frame_make_writable(encFrame), "webm_make_writable");
            ffmpeg.sws_scale(
                sws,
                decFrame->data,
                decFrame->linesize,
                0,
                decFrame->height,
                encFrame->data,
                encFrame->linesize);
            encFrame->pts = frameIndex++;

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, encFrame), "webm_send_frame");
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
            ThrowIfErr(gp, "webm_receive_packet");

            ffmpeg.av_packet_rescale_ts(outPkt, encCtx->time_base, outStream->time_base);
            outPkt->stream_index = outStream->index;
            ThrowIfErr(ffmpeg.av_interleaved_write_frame(outFmt, outPkt), "webm_write_packet");
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
