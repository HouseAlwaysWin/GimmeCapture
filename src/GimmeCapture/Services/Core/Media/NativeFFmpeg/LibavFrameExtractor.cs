using System;
using FFmpeg.AutoGen;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// Decodes a single video frame at an arbitrary timestamp into a standalone <see cref="SKBitmap"/>
/// (BGRA, premultiplied), for feeding per-frame SAM2 object tracking. Seeks backward to the nearest
/// keyframe, then decodes forward to the first frame at/after the target time. The returned bitmap owns
/// its pixels (copied to managed memory), so it stays valid after the native frame is freed.
/// </summary>
internal static class LibavFrameExtractor
{
    public static unsafe SKBitmap? GetFrameAt(string videoPath, double seconds, int outputWidth, int outputHeight)
    {
        if (string.IsNullOrEmpty(videoPath) || outputWidth <= 0 || outputHeight <= 0)
        {
            return null;
        }

        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* fmt = null;
        AVCodecContext* decCtx = null;
        SwsContext* sws = null;
        AVPacket* pkt = null;
        AVFrame* decFrame = null;
        AVFrame* outFrame = null;

        try
        {
            ThrowIfErr(ffmpeg.avformat_open_input(&fmt, videoPath, null, null), "open_input");
            ThrowIfErr(ffmpeg.avformat_find_stream_info(fmt, null), "find_stream_info");

            int videoStream = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (videoStream < 0)
            {
                return null;
            }

            AVStream* st = fmt->streams[videoStream];
            AVCodec* dec = ffmpeg.avcodec_find_decoder(st->codecpar->codec_id);
            if (dec == null)
            {
                return null;
            }

            decCtx = ffmpeg.avcodec_alloc_context3(dec);
            ThrowIfErr(ffmpeg.avcodec_parameters_to_context(decCtx, st->codecpar), "par_to_ctx");
            ThrowIfErr(ffmpeg.avcodec_open2(decCtx, dec, null), "open_decoder");

            pkt = ffmpeg.av_packet_alloc();
            decFrame = ffmpeg.av_frame_alloc();
            outFrame = ffmpeg.av_frame_alloc();
            if (pkt == null || decFrame == null || outFrame == null)
            {
                return null;
            }

            sws = ffmpeg.sws_getContext(
                decCtx->width, decCtx->height, decCtx->pix_fmt,
                outputWidth, outputHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (sws == null)
            {
                return null;
            }

            outFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            outFrame->width = outputWidth;
            outFrame->height = outputHeight;
            ThrowIfErr(ffmpeg.av_frame_get_buffer(outFrame, 1), "out_frame_buffer");
            // outFrame is exclusively owned and never shared with the decoder, so one make-writable suffices.
            ThrowIfErr(ffmpeg.av_frame_make_writable(outFrame), "out_frame_writable");

            if (seconds > 0)
            {
                long ts = (long)Math.Max(0, seconds / ffmpeg.av_q2d(st->time_base));
                if (ffmpeg.av_seek_frame(fmt, videoStream, ts, ffmpeg.AVSEEK_FLAG_BACKWARD) >= 0)
                {
                    ffmpeg.avcodec_flush_buffers(decCtx);
                }
            }

            bool draining = false;
            while (true)
            {
                if (!draining)
                {
                    int rr = ffmpeg.av_read_frame(fmt, pkt);
                    if (rr == ffmpeg.AVERROR_EOF)
                    {
                        ffmpeg.avcodec_send_packet(decCtx, (AVPacket*)null);
                        draining = true;
                    }
                    else if (rr < 0)
                    {
                        if (rr == ffmpeg.AVERROR(11))
                        {
                            continue;
                        }

                        ThrowIfErr(rr, "read_frame");
                    }
                    else
                    {
                        if (pkt->stream_index != videoStream)
                        {
                            ffmpeg.av_packet_unref(pkt);
                            continue;
                        }

                        ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, pkt), "send_packet");
                        ffmpeg.av_packet_unref(pkt);
                    }
                }

                int gr = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
                if (gr == ffmpeg.AVERROR(11))
                {
                    if (draining)
                    {
                        break;
                    }

                    continue;
                }

                if (gr == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                ThrowIfErr(gr, "receive_frame");

                long fts = decFrame->best_effort_timestamp;
                double fsec = fts >= 0 ? fts * ffmpeg.av_q2d(st->time_base) : 0;
                if (fsec + 0.0005 < seconds)
                {
                    ffmpeg.av_frame_unref(decFrame);
                    continue; // still before the target — keep decoding forward
                }

                ffmpeg.sws_scale(sws, decFrame->data, decFrame->linesize, 0, decCtx->height, outFrame->data, outFrame->linesize);
                ffmpeg.av_frame_unref(decFrame);
                return CopyToSkBitmap(outFrame, outputWidth, outputHeight);
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (pkt != null) ffmpeg.av_packet_free(&pkt);
            if (decFrame != null) ffmpeg.av_frame_free(&decFrame);
            if (outFrame != null) ffmpeg.av_frame_free(&outFrame);
            if (sws != null) ffmpeg.sws_freeContext(sws);
            if (decCtx != null) ffmpeg.avcodec_free_context(&decCtx);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }

    private static unsafe SKBitmap CopyToSkBitmap(AVFrame* bgra, int width, int height)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        byte* dstBase = (byte*)bmp.GetPixels();
        int dstStride = bmp.RowBytes;
        int srcStride = bgra->linesize[0];
        // BGRA: source linesize is always >= width*4 (may be padded); copy only the real width, and bound
        // the destination size by its actual stride so a padded/odd dst row can't be overrun.
        int copyBytes = Math.Min(width * 4, Math.Min(srcStride, dstStride));
        for (int y = 0; y < height; y++)
        {
            byte* src = bgra->data[0] + (y * srcStride);
            byte* dst = dstBase + (y * dstStride);
            Buffer.MemoryCopy(src, dst, dstStride, copyBytes);
        }

        return bmp;
    }

    private static void ThrowIfErr(int err, string ctx)
    {
        if (err < 0) throw FFmpegErrors.ToException(err, ctx);
    }
}
