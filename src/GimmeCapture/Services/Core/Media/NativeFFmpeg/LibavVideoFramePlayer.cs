using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
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

    // Decodes a single frame at (or just before) the given timestamp as BGRA bytes scaled to width×height.
    // Returns null if no frame is produced or it is cancelled. Used for paused frame-step / seek in compare.
    public static async Task<byte[]?> DecodeFrameAtAsync(
        string videoPath, double seconds, int width, int height, CancellationToken ct = default)
    {
        byte[]? frame = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            using var player = new LibavVideoFramePlayer();
            await player.PlayAsync(videoPath, width, height, Math.Max(0, seconds), 1.0, false, (data, _) =>
            {
                if (frame == null)
                {
                    frame = (byte[])data.Clone(); // first frame at/after the seek point
                    stop.Cancel();
                }
            }, stop.Token);
        }
        catch (OperationCanceledException)
        {
            // expected — we cancel right after grabbing the first frame
        }

        return frame;
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

                    double loopStartSeconds = Math.Max(0, startSeconds);
                    int frameSize = outputWidth * outputHeight * 4;
                    byte[] frameBuffer = ArrayPool<byte>.Shared.Rent(frameSize);
                    bool decoderDraining = false;
                    bool shouldResetLoop = false;
                    double startedAtSeconds = -1;
                    var playbackClock = Stopwatch.StartNew();

                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            if (!decoderDraining)
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

                            while (!ct.IsCancellationRequested)
                            {
                                int gr = ffmpeg.avcodec_receive_frame(decCtx, decFrame);
                                if (gr == ffmpeg.AVERROR(11))
                                {
                                    break;
                                }

                                if (gr == ffmpeg.AVERROR_EOF)
                                {
                                    shouldResetLoop = loop;
                                    break;
                                }

                                ThrowIfErr(gr, "receive_frame");

                                long ts = decFrame->best_effort_timestamp;
                                double seconds = ts >= 0 ? ts * ffmpeg.av_q2d(st->time_base) : 0;

                                if (seconds + 0.0005 < startSeconds)
                                {
                                    continue;
                                }

                                if (startedAtSeconds < 0)
                                {
                                    startedAtSeconds = seconds;
                                    playbackClock.Restart();
                                }

                                DelayUntilFrame(playbackClock, startedAtSeconds, seconds, speed, ct);
                                if (ct.IsCancellationRequested)
                                {
                                    break;
                                }

                                ThrowIfErr(ffmpeg.av_frame_make_writable(outFrame), "out_frame_writable");
                                ffmpeg.sws_scale(
                                    sws,
                                    decFrame->data,
                                    decFrame->linesize,
                                    0,
                                    decCtx->height,
                                    outFrame->data,
                                    outFrame->linesize);

                                CopyBgraFrame(outFrame, outputWidth, outputHeight, frameBuffer.AsSpan(0, frameSize));
                                onFrame(frameBuffer, seconds);
                            }

                            if (shouldResetLoop && !ct.IsCancellationRequested)
                            {
                                decoderDraining = false;
                                shouldResetLoop = false;
                                startedAtSeconds = -1;
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
                        ArrayPool<byte>.Shared.Return(frameBuffer);
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

    private static void DelayUntilFrame(Stopwatch playbackClock, double startedAtSeconds, double frameSeconds, double speed, CancellationToken ct)
    {
        double safeSpeed = Math.Max(0.1, speed);
        double targetSeconds = Math.Max(0, frameSeconds - startedAtSeconds) / safeSpeed;

        while (!ct.IsCancellationRequested)
        {
            double remainingSeconds = targetSeconds - playbackClock.Elapsed.TotalSeconds;
            if (remainingSeconds <= 0)
            {
                return;
            }

            if (remainingSeconds > 0.02)
            {
                Thread.Sleep(Math.Max(1, (int)Math.Floor((remainingSeconds - 0.01) * 1000)));
                continue;
            }

            Thread.SpinWait(256);
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
