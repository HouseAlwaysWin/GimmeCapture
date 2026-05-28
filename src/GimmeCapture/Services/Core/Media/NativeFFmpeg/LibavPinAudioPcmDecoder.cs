using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

    internal static unsafe DecodeResult Decode(string path, double startSeconds)
    {
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

            int sampleRate = decCtx->sample_rate;
            if (sampleRate <= 0)
            {
                sampleRate = par->sample_rate;
            }

            if (sampleRate <= 0)
            {
                sampleRate = 48000;
            }

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

            using var pcmBuffer = new PooledByteAccumulator();
            long startSample = startSeconds > 0 ? (long)Math.Round(startSeconds * sampleRate) : 0;

            while (true)
            {
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

                ThrowIfErr(ffmpeg.avcodec_send_packet(decCtx, pkt), "pin_audio_send_packet");
                ffmpeg.av_packet_unref(pkt);

                ReceiveAndConvert(decCtx, swr, frame, outFrm, outLayout, ast->time_base, startSample, pcmBuffer);
            }

            _ = ffmpeg.avcodec_send_packet(decCtx, (AVPacket*)null);
            ReceiveDraining(decCtx, swr, frame, outFrm, outLayout, ast->time_base, startSample, pcmBuffer);
            DrainSwrTail(swr, outFrm, outLayout, pcmBuffer, sampleRate);

            var wf = new WaveFormat(sampleRate, 16, 2);
            return new DecodeResult(pcmBuffer.ToArray(), wf);
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
        PooledByteAccumulator pcmBuffer)
    {
        while (true)
        {
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
        PooledByteAccumulator pcmBuffer)
    {
        while (true)
        {
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
        PooledByteAccumulator pcmBuffer,
        int sampleRate)
    {
        int sr = sampleRate > 0 ? sampleRate : 48000;
        for (int i = 0; i < 64; i++)
        {
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

    private static unsafe void AppendConvertedPlain(AVFrame* outFrm, PooledByteAccumulator pcmBuffer, int skipSamples = 0)
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
        PooledByteAccumulator pcmBuffer)
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

    private sealed class PooledByteAccumulator : IDisposable
    {
        private byte[] _buffer = Array.Empty<byte>();
        private int _length;

        public unsafe void Append(byte* source, int byteLength)
        {
            EnsureCapacity(_length + byteLength);
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

        private void EnsureCapacity(int required)
        {
            if (_buffer.Length >= required)
            {
                return;
            }

            int newSize = _buffer.Length == 0 ? 16 * 1024 : _buffer.Length;
            while (newSize < required)
            {
                newSize *= 2;
            }

            byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
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
