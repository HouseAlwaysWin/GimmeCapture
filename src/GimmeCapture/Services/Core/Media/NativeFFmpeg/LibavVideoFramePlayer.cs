using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

internal sealed class LibavVideoFramePlayer : IDisposable
{
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

    public async Task PlayAsync(
        string videoPath,
        int outputWidth,
        int outputHeight,
        double startSeconds,
        double speed,
        bool loop,
        Action<byte[], double> onFrame,
        CancellationToken ct)
    {
        await Task.Run(() =>
        {
            FFmpegRuntime.EnsureInitialized();
            unsafe
            {
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
                        throw new InvalidOperationException("No video stream.");

                    AVStream* st = fmt->streams[videoStream];
                    AVCodecParameters* par = st->codecpar;
                    AVCodec* dec = ffmpeg.avcodec_find_decoder(par->codec_id);
                    if (dec == null)
                        throw new InvalidOperationException($"Decoder not found for {par->codec_id}");

                    decCtx = ffmpeg.avcodec_alloc_context3(dec);
                    ThrowIfErr(ffmpeg.avcodec_parameters_to_context(decCtx, par), "par_to_ctx");
                    ThrowIfErr(ffmpeg.avcodec_open2(decCtx, dec, null), "open_decoder");

                    pkt = ffmpeg.av_packet_alloc();
                    decFrame = ffmpeg.av_frame_alloc();
                    outFrame = ffmpeg.av_frame_alloc();
                    if (pkt == null || decFrame == null || outFrame == null)
                        throw new OutOfMemoryException("alloc packet/frame");

                    sws = ffmpeg.sws_getContext(
                        decCtx->width, decCtx->height, decCtx->pix_fmt,
                        outputWidth, outputHeight, AVPixelFormat.AV_PIX_FMT_BGRA,
                        (int)SwsFlags.SWS_BILINEAR, null, null, null);
                    if (sws == null)
                        throw new InvalidOperationException("sws_getContext failed.");

                    outFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                    outFrame->width = outputWidth;
                    outFrame->height = outputHeight;
                    ThrowIfErr(ffmpeg.av_frame_get_buffer(outFrame, 1), "out_frame_buffer");

                    if (startSeconds > 0)
                    {
                        Seek(fmt, decCtx, videoStream, st->time_base, startSeconds);
                    }

                    int frameDelayMs = Math.Max(1, (int)Math.Round((1000.0 / 30.0) / Math.Max(0.1, speed)));
                    byte[] frameBuffer = new byte[outputWidth * outputHeight * 4];

                    while (!ct.IsCancellationRequested)
                    {
                        int rr = ffmpeg.av_read_frame(fmt, pkt);
                        if (rr == ffmpeg.AVERROR_EOF)
                        {
                            if (!loop) break;
                            Seek(fmt, decCtx, videoStream, st->time_base, 0);
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

                        while (!ct.IsCancellationRequested)
                        {
                            int gr = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
                            if (gr == ffmpeg.AVERROR(11) || gr == ffmpeg.AVERROR_EOF) break;
                            ThrowIfErr(gr, "receive_frame");

                            ThrowIfErr(ffmpeg.av_frame_make_writable(outFrame), "out_frame_writable");
                            ffmpeg.sws_scale(
                                sws,
                                decFrame->data,
                                decFrame->linesize,
                                0,
                                decCtx->height,
                                outFrame->data,
                                outFrame->linesize);

                            CopyBgraFrame(outFrame, outputWidth, outputHeight, frameBuffer);

                            long ts = decFrame->best_effort_timestamp;
                            double seconds = ts >= 0 ? ts * ffmpeg.av_q2d(st->time_base) : 0;
                            onFrame(frameBuffer, seconds);

                            Thread.Sleep(frameDelayMs);
                        }
                    }
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
        }, ct).ConfigureAwait(false);
    }

    private static unsafe void Seek(AVFormatContext* fmt, AVCodecContext* decCtx, int streamIndex, AVRational timeBase, double seconds)
    {
        long ts = (long)Math.Max(0, seconds / ffmpeg.av_q2d(timeBase));
        int sr = ffmpeg.av_seek_frame(fmt, streamIndex, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ThrowIfErr(sr, "seek_frame");
        ffmpeg.avcodec_flush_buffers(decCtx);
    }

    private static unsafe void CopyBgraFrame(AVFrame* frame, int width, int height, byte[] output)
    {
        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
        {
            IntPtr src = (IntPtr)(frame->data[0] + y * frame->linesize[0]);
            Marshal.Copy(src, output, y * rowBytes, rowBytes);
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
