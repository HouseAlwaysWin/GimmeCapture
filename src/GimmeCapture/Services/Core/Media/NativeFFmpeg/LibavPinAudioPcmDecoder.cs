using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;
using NAudio.Wave;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// PCM decode for pin preview: decode from <paramref name="startSeconds"/> to EOF with proper
/// codec / swresample draining so the last frames are not cut off (fixes missing audio at end).
/// </summary>
internal static class LibavPinAudioPcmDecoder
{
    internal sealed record DecodeResult(byte[] PcmBytes, WaveFormat WaveFormat);

    // Output is always 16-bit stereo.
    private const int OutputBytesPerSampleFrame = 4;

    // How far past a range's end the demuxer may read before giving up on filling it, for audio with a hole in it.
    private const double ReadPastRangeEndSeconds = 1.0;

    private const int InitialPcmCapacity = 16 * 1024;

    internal static DecodeResult Decode(string path, double startSeconds, CancellationToken cancellationToken = default) =>
        DecodeRange(path, startSeconds, double.PositiveInfinity, cancellationToken);

    /// <summary>
    /// Decodes <paramref name="durationSeconds"/> of audio from <paramref name="startSeconds"/>, and stops reading the
    /// file there instead of decoding on to its end. The clip exporter builds an export's audio one kept range at a
    /// time with it; decoding to the end and keeping only the range cost a trim with many ranges a decode of the rest of
    /// the file for each one.
    /// </summary>
    internal static DecodeResult DecodeRange(
        string path, double startSeconds, double durationSeconds, CancellationToken cancellationToken = default)
    {
        using var accumulator = new PooledByteAccumulator();
        WaveFormat wf = DecodeCore(path, startSeconds, durationSeconds, onFormat: null, accumulator, waitIfFull: null, cancellationToken);
        return new DecodeResult(accumulator.ToArray(), wf);
    }

    /// <summary>
    /// The PCM a range of <paramref name="durationSeconds"/> keeps at <paramref name="sampleRate"/>, rounded to whole
    /// sample frames the way the clip exporter has always cut it. Unbounded for an open-ended decode.
    /// </summary>
    internal static long RangeByteLimit(double durationSeconds, int sampleRate)
    {
        if (double.IsPositiveInfinity(durationSeconds))
        {
            return long.MaxValue;
        }

        long bytes = (long)Math.Round(Math.Max(0, durationSeconds) * sampleRate * OutputBytesPerSampleFrame);
        return bytes - (bytes % OutputBytesPerSampleFrame);
    }

    /// <summary>
    /// The size to grow a PCM buffer of <paramref name="currentCapacity"/> bytes to so it holds <paramref name="required"/>:
    /// doubling from 16 KiB, worked out in long arithmetic and capped at the largest array .NET allows. Doubled as an int,
    /// the size wrapped to int.MinValue and then 0 once it passed 1 GiB (about 93 minutes of 48 kHz stereo), and the loop
    /// growing it spun forever. Past the largest array it now says so instead.
    /// </summary>
    internal static int GrowPcmCapacity(int currentCapacity, long required)
    {
        if (required <= currentCapacity)
        {
            return currentCapacity;
        }

        if (required > Array.MaxLength)
        {
            throw new InvalidOperationException(
                $"The decoded audio needs {required:N0} bytes, more than one buffer can hold ({Array.MaxLength:N0} bytes).");
        }

        long size = Math.Max(currentCapacity, InitialPcmCapacity);
        while (size < required)
        {
            size *= 2;
        }

        return (int)Math.Min(size, Array.MaxLength);
    }

    /// <summary>
    /// Streaming variant for preview playback: decodes on the caller's thread and pushes each converted
    /// S16-stereo chunk to <paramref name="onPcm"/> (the buffer is reused — copy out synchronously).
    /// <paramref name="onFormat"/> fires once with the output WaveFormat before any chunk; <paramref
    /// name="waitIfFull"/> applies consumer backpressure so memory stays bounded. Unlike <see cref="Decode"/>
    /// it never buffers the whole file.
    /// </summary>
    internal static void DecodeStreaming(
        string path,
        double startSeconds,
        Action<WaveFormat> onFormat,
        Action<byte[], int> onPcm,
        Action? waitIfFull,
        CancellationToken cancellationToken)
    {
        using var sink = new StreamingPcmSink(onPcm);
        DecodeCore(path, startSeconds, double.PositiveInfinity, onFormat, sink, waitIfFull, cancellationToken);
    }

    private static unsafe WaveFormat DecodeCore(
        string path,
        double startSeconds,
        double durationSeconds,
        Action<WaveFormat>? onFormat,
        IPcmChunkSink output,
        Action? waitIfFull,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* fmt = null;
        AVCodecContext* decCtx = null;
        SwrContext* swr = null;
        AVPacket* pkt = null;
        AVFrame* frame = null;
        AVFrame* outFrm = null;
        AVChannelLayout outLayout = default;

        try
        {
            ThrowIfErr(ffmpeg.avformat_open_input(&fmt, path, null, null), "pin_audio_open");
            ThrowIfErr(ffmpeg.avformat_find_stream_info(fmt, null), "pin_audio_find_stream_info");

            int audioIx = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (audioIx < 0)
            {
                throw new InvalidOperationException("No audio stream.");
            }

            // Only the audio is wanted, so the demuxer skips the other streams' packets (a video's, mostly) rather than
            // reading each one in for this loop to throw away.
            for (int i = 0; i < (int)fmt->nb_streams; i++)
            {
                if (i != audioIx)
                {
                    fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
                }
            }

            AVStream* ast = fmt->streams[audioIx];
            AVCodecParameters* par = ast->codecpar;
            AVCodec* dec = ffmpeg.avcodec_find_decoder(par->codec_id);
            if (dec == null)
            {
                throw new InvalidOperationException($"No decoder for {par->codec_id}");
            }

            decCtx = ffmpeg.avcodec_alloc_context3(dec);
            ThrowIfErr(ffmpeg.avcodec_parameters_to_context(decCtx, par), "pin_audio_par_to_ctx");
            ThrowIfErr(ffmpeg.avcodec_open2(decCtx, dec, null), "pin_audio_open_decoder");

            // A stream that does not say which speaker each channel feeds (a plain WAV, PCM in Matroska or AVI) decodes
            // with an unspecified layout. swresample takes the default layout for that many channels instead and then
            // refuses every frame as "Input changed". Naming the default layout on the decoder makes the two agree.
            if (decCtx->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC && decCtx->ch_layout.nb_channels > 0)
            {
                int channels = decCtx->ch_layout.nb_channels;
                ffmpeg.av_channel_layout_uninit(&decCtx->ch_layout);
                ffmpeg.av_channel_layout_default(&decCtx->ch_layout, channels);
            }

            int sampleRate = decCtx->sample_rate;
            if (sampleRate <= 0)
            {
                sampleRate = par->sample_rate;
            }

            if (sampleRate <= 0)
            {
                sampleRate = 48000;
            }

            // Output format is known now — signal it so a streaming consumer can start the device before any
            // PCM arrives (first audio in ~100–200 ms instead of after a full-file decode).
            var waveFormat = new WaveFormat(sampleRate, 16, 2);
            onFormat?.Invoke(waveFormat);

            ffmpeg.av_channel_layout_default(&outLayout, 2);
            ThrowIfErr(ffmpeg.swr_alloc_set_opts2(
                    &swr,
                    &outLayout,
                    AVSampleFormat.AV_SAMPLE_FMT_S16,
                    sampleRate,
                    &decCtx->ch_layout,
                    decCtx->sample_fmt,
                    sampleRate,
                    0,
                    null),
                "pin_audio_swr_opts");

            ThrowIfErr(ffmpeg.swr_init(swr), "pin_audio_swr_init");

            pkt = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
            outFrm = ffmpeg.av_frame_alloc();
            if (pkt == null || frame == null || outFrm == null)
            {
                throw new OutOfMemoryException("pin_audio_alloc");
            }

            if (startSeconds > 0)
            {
                long ts = (long)(startSeconds / ffmpeg.av_q2d(ast->time_base));
                int sr = ffmpeg.av_seek_frame(fmt, audioIx, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (sr >= 0)
                {
                    ffmpeg.avcodec_flush_buffers(decCtx);
                }
            }

            long startSample = startSeconds > 0 ? (long)Math.Round(startSeconds * sampleRate) : 0;

            // A range is done once its samples are in. Reading also stops a little past its end, so audio with a hole in
            // it does not send the demuxer on to the end of the file looking for samples that are not there.
            var pcmBuffer = new RangeLimitedSink(output, RangeByteLimit(durationSeconds, sampleRate));
            double stopReadingAtSeconds = startSeconds + durationSeconds + ReadPastRangeEndSeconds;

            while (!pcmBuffer.IsFull)
            {
                cancellationToken.ThrowIfCancellationRequested();
                waitIfFull?.Invoke(); // consumer backpressure — bounds memory in the streaming path (no-op for Decode)
                int rr = ffmpeg.av_read_frame(fmt, pkt);
                if (rr == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                if (rr == ffmpeg.AVERROR(11))
                {
                    System.Threading.Thread.Sleep(2);
                    continue;
                }

                ThrowIfErr(rr, "pin_audio_read_frame");

                if (pkt->stream_index != audioIx)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                long packetTs = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
                if (packetTs != ffmpeg.AV_NOPTS_VALUE && packetTs * ffmpeg.av_q2d(ast->time_base) > stopReadingAtSeconds)
                {
                    ffmpeg.av_packet_unref(pkt);
                    break;
                }

                ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, pkt), "pin_audio_send_packet");
                ffmpeg.av_packet_unref(pkt);

                ReceiveAndConvert(decCtx, swr, frame, outFrm, outLayout, ast->time_base, startSample, pcmBuffer, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = ffmpeg.avcodec_send_packet(decCtx, (AVPacket*)null);
            ReceiveDraining(decCtx, swr, frame, outFrm, outLayout, ast->time_base, startSample, pcmBuffer, cancellationToken);
            DrainSwrTail(swr, outFrm, outLayout, pcmBuffer, sampleRate, cancellationToken);

            return waveFormat;
        }
        finally
        {
            ffmpeg.av_channel_layout_uninit(&outLayout);

            if (pkt != null)
            {
                ffmpeg.av_packet_free(&pkt);
            }

            if (frame != null)
            {
                ffmpeg.av_frame_free(&frame);
            }

            if (outFrm != null)
            {
                ffmpeg.av_frame_free(&outFrm);
            }

            if (swr != null)
            {
                ffmpeg.swr_free(&swr);
            }

            if (decCtx != null)
            {
                ffmpeg.avcodec_free_context(&decCtx);
            }

            if (fmt != null)
            {
                ffmpeg.avformat_close_input(&fmt);
            }
        }
    }

    private static unsafe void ReceiveAndConvert(
        AVCodecContext* decCtx,
        SwrContext* swr,
        AVFrame* frame,
        AVFrame* outFrm,
        AVChannelLayout outLayout,
        AVRational streamTimeBase,
        long startSample,
        IPcmChunkSink pcmBuffer,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int gr = ffmpeg.avcodec_receive_frame(decCtx, frame);
            if (gr == ffmpeg.AVERROR(11) || gr == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            ThrowIfErr(gr, "pin_audio_receive_frame");
            AppendConverted(swr, frame, outFrm, outLayout, streamTimeBase, startSample, pcmBuffer);
        }
    }

    private static unsafe void ReceiveDraining(
        AVCodecContext* decCtx,
        SwrContext* swr,
        AVFrame* frame,
        AVFrame* outFrm,
        AVChannelLayout outLayout,
        AVRational streamTimeBase,
        long startSample,
        IPcmChunkSink pcmBuffer,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int gr = ffmpeg.avcodec_receive_frame(decCtx, frame);
            if (gr == ffmpeg.AVERROR_EOF)
            {
                break;
            }

            if (gr == ffmpeg.AVERROR(11))
            {
                break;
            }

            ThrowIfErr(gr, "pin_audio_receive_draining");
            AppendConverted(swr, frame, outFrm, outLayout, streamTimeBase, startSample, pcmBuffer);
        }
    }

    private static unsafe void DrainSwrTail(
        SwrContext* swr,
        AVFrame* outFrm,
        AVChannelLayout outLayout,
        IPcmChunkSink pcmBuffer,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        int sr = sampleRate > 0 ? sampleRate : 48000;
        for (int i = 0; i < 64; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ffmpeg.av_channel_layout_copy(&outFrm->ch_layout, &outLayout);
            outFrm->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
            outFrm->sample_rate = sr;
            int rc = ffmpeg.swr_convert_frame(swr, outFrm, null);
            if (rc < 0 && rc != ffmpeg.AVERROR_EOF && rc != ffmpeg.AVERROR(22))
            {
                break;
            }

            if (outFrm->nb_samples <= 0 || outFrm->data[0] == null)
            {
                break;
            }

            AppendConvertedPlain(outFrm, pcmBuffer);
            ffmpeg.av_frame_unref(outFrm);
            if (rc < 0)
            {
                break;
            }
        }
    }

    private static unsafe void AppendConvertedPlain(AVFrame* outFrm, IPcmChunkSink pcmBuffer, int skipSamples = 0)
    {
        int nb = outFrm->nb_samples;
        int ch = outFrm->ch_layout.nb_channels > 0 ? outFrm->ch_layout.nb_channels : 2;
        int clampedSkipSamples = Math.Clamp(skipSamples, 0, nb);
        int bytesPerSample = ch * 2;
        int bytes = (nb - clampedSkipSamples) * bytesPerSample;
        if (bytes <= 0 || outFrm->data[0] == null)
        {
            return;
        }

        pcmBuffer.Append((byte*)outFrm->data[0] + (clampedSkipSamples * bytesPerSample), bytes);
    }

    private static unsafe void AppendConverted(
        SwrContext* swr,
        AVFrame* frame,
        AVFrame* outFrm,
        AVChannelLayout outLayout,
        AVRational streamTimeBase,
        long startSample,
        IPcmChunkSink pcmBuffer)
    {
        ffmpeg.av_channel_layout_copy(&outFrm->ch_layout, &outLayout);
        outFrm->sample_rate = frame->sample_rate > 0 ? frame->sample_rate : outFrm->sample_rate;
        outFrm->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
        outFrm->nb_samples = frame->nb_samples;

        int rc = ffmpeg.swr_convert_frame(swr, outFrm, frame);
        ThrowIfErr(rc, "pin_audio_swr_convert_frame");

        int skipSamples = 0;
        long frameTs = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE
            ? frame->best_effort_timestamp
            : frame->pts;
        if (frameTs != ffmpeg.AV_NOPTS_VALUE && startSample > 0 && outFrm->sample_rate > 0)
        {
            double frameStartSeconds = frameTs * ffmpeg.av_q2d(streamTimeBase);
            long frameStartSample = (long)Math.Round(frameStartSeconds * outFrm->sample_rate);
            long sampleDelta = startSample - frameStartSample;
            if (sampleDelta > 0)
            {
                skipSamples = (int)Math.Min(sampleDelta, outFrm->nb_samples);
            }
        }

        AppendConvertedPlain(outFrm, pcmBuffer, skipSamples);
        ffmpeg.av_frame_unref(outFrm);
    }

    private static void ThrowIfErr(int err, string ctx)
    {
        if (err < 0)
        {
            throw FFmpegErrors.ToException(err, ctx);
        }
    }

    /// <summary>Sink for converted S16 PCM chunks — either the full-file accumulator or the streaming pump.</summary>
    private interface IPcmChunkSink
    {
        unsafe void Append(byte* data, int byteLength);
    }

    /// <summary>Passes converted chunks on until a range's bytes are in, and drops the rest.</summary>
    private sealed class RangeLimitedSink : IPcmChunkSink
    {
        private readonly IPcmChunkSink _inner;
        private long _remaining;

        public RangeLimitedSink(IPcmChunkSink inner, long maxBytes)
        {
            _inner = inner;
            _remaining = maxBytes;
        }

        public bool IsFull => _remaining <= 0;

        public unsafe void Append(byte* data, int byteLength)
        {
            int take = (int)Math.Min(byteLength, _remaining);
            if (take <= 0)
            {
                return;
            }

            _inner.Append(data, take);
            _remaining -= take;
        }
    }

    /// <summary>Streaming sink: copies each converted chunk into a reusable managed buffer and hands it to the
    /// consumer synchronously. No full-file buffering.</summary>
    private sealed class StreamingPcmSink : IPcmChunkSink, IDisposable
    {
        private readonly Action<byte[], int> _onPcm;
        private byte[] _scratch = ArrayPool<byte>.Shared.Rent(64 * 1024);

        public StreamingPcmSink(Action<byte[], int> onPcm) => _onPcm = onPcm;

        public unsafe void Append(byte* data, int byteLength)
        {
            if (byteLength <= 0 || data == null)
            {
                return;
            }

            if (_scratch.Length < byteLength)
            {
                ArrayPool<byte>.Shared.Return(_scratch);
                _scratch = ArrayPool<byte>.Shared.Rent(byteLength);
            }

            fixed (byte* dst = _scratch)
            {
                Buffer.MemoryCopy(data, dst, _scratch.Length, byteLength);
            }

            // The consumer (BufferedWaveProvider.AddSamples) copies synchronously, so reusing _scratch is safe.
            _onPcm(_scratch, byteLength);
        }

        public void Dispose()
        {
            if (_scratch.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_scratch);
                _scratch = Array.Empty<byte>();
            }
        }
    }

    private sealed class PooledByteAccumulator : IPcmChunkSink, IDisposable
    {
        private byte[] _buffer = Array.Empty<byte>();
        private int _length;

        public unsafe void Append(byte* source, int byteLength)
        {
            EnsureCapacity((long)_length + byteLength);
            fixed (byte* destination = &_buffer[_length])
            {
                Buffer.MemoryCopy(source, destination, _buffer.Length - _length, byteLength);
            }

            _length += byteLength;
        }

        public byte[] ToArray()
        {
            if (_length == 0)
            {
                return Array.Empty<byte>();
            }

            var result = new byte[_length];
            _buffer.AsSpan(0, _length).CopyTo(result);
            return result;
        }

        private void EnsureCapacity(long required)
        {
            if (_buffer.Length >= required)
            {
                return;
            }

            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(GrowPcmCapacity(_buffer.Length, required));
            if (_length > 0)
            {
                _buffer.AsSpan(0, _length).CopyTo(newBuffer);
            }

            if (_buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }

            _buffer = newBuffer;
        }

        public void Dispose()
        {
            if (_buffer.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = Array.Empty<byte>();
            }

            _length = 0;
        }
    }
}
