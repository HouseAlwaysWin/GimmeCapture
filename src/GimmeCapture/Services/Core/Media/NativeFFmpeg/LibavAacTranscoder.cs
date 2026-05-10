using System;
using FFmpeg.AutoGen;
using GimmeCapture.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// WAV -> AAC-in-MP4/M4A for muxing into MP4/MOV outputs (native libav*, no Media Foundation / no ffmpeg.exe).
/// </summary>
internal static class LibavAacTranscoder
{
    public static unsafe void EncodeWavToM4a(string wavPath, string m4aPath, VideoQuality quality)
    {
        FFmpegRuntime.EnsureInitialized();

        using var wavReader = new WaveFileReader(wavPath);
        ISampleProvider provider = CreateNormalizedSampleProvider(wavReader);

        AVFormatContext* outFmt = null;
        AVCodecContext* encCtx = null;
        AVPacket* outPkt = null;
        AVStream* outStream = null;
        AVChannelLayout stereoLayout = default;
        ffmpeg.av_channel_layout_default(&stereoLayout, 2);

        try
        {
            ThrowIfErr(ffmpeg.avformat_alloc_output_context2(&outFmt, null, "mp4", m4aPath), "aac_alloc_out");
            if (outFmt == null)
            {
                throw new InvalidOperationException("Failed to create M4A output context.");
            }

            AVCodec* enc = ffmpeg.avcodec_find_encoder_by_name("aac");
            if (enc == null)
            {
                enc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
            }

            if (enc == null)
            {
                throw new InvalidOperationException("AAC encoder unavailable.");
            }

            encCtx = ffmpeg.avcodec_alloc_context3(enc);
            if (encCtx == null)
            {
                throw new OutOfMemoryException("aac_alloc_enc");
            }

            encCtx->sample_rate = provider.WaveFormat.SampleRate;
            encCtx->time_base = new AVRational { num = 1, den = encCtx->sample_rate };
            ThrowIfErr(ffmpeg.av_channel_layout_copy(&encCtx->ch_layout, &stereoLayout), "aac_enc_layout");
            encCtx->sample_fmt = ChooseSampleFormat(enc);
            encCtx->bit_rate = quality switch
            {
                VideoQuality.High => 192_000,
                VideoQuality.Low => 96_000,
                _ => 128_000,
            };

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                encCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            ThrowIfErr(ffmpeg.avcodec_open2(encCtx, enc, null), "aac_open_enc");

            outStream = ffmpeg.avformat_new_stream(outFmt, null);
            if (outStream == null)
            {
                throw new OutOfMemoryException("aac_new_stream");
            }

            ThrowIfErr(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encCtx), "aac_par_from_ctx");
            outStream->codecpar->codec_tag = 0;
            outStream->time_base = encCtx->time_base;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ThrowIfErr(ffmpeg.avio_open(&outFmt->pb, m4aPath, ffmpeg.AVIO_FLAG_WRITE), "aac_avio_open");
            }

            ThrowIfErr(ffmpeg.avformat_write_header(outFmt, null), "aac_write_header");

            outPkt = ffmpeg.av_packet_alloc();
            if (outPkt == null)
            {
                throw new OutOfMemoryException("aac_alloc_pkt");
            }

            int channels = provider.WaveFormat.Channels;
            int frameSamples = encCtx->frame_size > 0 ? encCtx->frame_size : 1024;
            float[] sampleBuffer = new float[frameSamples * channels];
            long ptsSamples = 0;

            while (true)
            {
                int floatsRead = ReadFully(provider, sampleBuffer);
                if (floatsRead <= 0)
                {
                    break;
                }

                if (floatsRead % channels != 0)
                {
                    throw new InvalidOperationException("Audio sample provider returned a partial frame.");
                }

                int samplesReadPerChannel = floatsRead / channels;
                if (floatsRead < sampleBuffer.Length)
                {
                    Array.Clear(sampleBuffer, floatsRead, sampleBuffer.Length - floatsRead);
                }

                AVFrame* frame = CreateAudioFrame(encCtx, &stereoLayout, frameSamples);
                try
                {
                    WriteSamplesToFrame(frame, encCtx->sample_fmt, sampleBuffer, frameSamples, channels);
                    frame->pts = ptsSamples;
                    ptsSamples += samplesReadPerChannel;
                    ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, frame), "aac_send_frame");
                    WriteEncodedPackets(encCtx, outFmt, outStream, outPkt);
                }
                finally
                {
                    if (frame != null)
                    {
                        ffmpeg.av_frame_free(&frame);
                    }
                }
            }

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, null), "aac_flush_enc");
            WriteEncodedPackets(encCtx, outFmt, outStream, outPkt);

            ffmpeg.av_write_trailer(outFmt);
        }
        finally
        {
            if (outPkt != null)
            {
                ffmpeg.av_packet_free(&outPkt);
            }

            if (encCtx != null)
            {
                ffmpeg.avcodec_free_context(&encCtx);
            }

            if (outFmt != null)
            {
                if (outFmt->pb != null && (outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                {
                    ffmpeg.avio_closep(&outFmt->pb);
                }

                ffmpeg.avformat_free_context(outFmt);
            }

            ffmpeg.av_channel_layout_uninit(&stereoLayout);
        }
    }

    private static ISampleProvider CreateNormalizedSampleProvider(WaveFileReader reader)
    {
        ISampleProvider provider = reader.ToSampleProvider();

        provider = provider.WaveFormat.Channels switch
        {
            1 => provider.ToStereo(1f, 1f),
            2 => provider,
            > 2 => DownmixToStereo(provider),
            _ => throw new InvalidOperationException($"Unsupported channel count: {provider.WaveFormat.Channels}")
        };

        if (provider.WaveFormat.SampleRate != 48_000)
        {
            provider = new WdlResamplingSampleProvider(provider, 48_000);
        }

        return provider;
    }

    private static ISampleProvider DownmixToStereo(ISampleProvider provider)
    {
        var mux = new MultiplexingSampleProvider(new[] { provider }, 2);
        mux.ConnectInputToOutput(0, 0);
        mux.ConnectInputToOutput(1, 1);
        return mux;
    }

    private static int ReadFully(ISampleProvider provider, float[] buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = provider.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read <= 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead;
    }

    private static unsafe AVFrame* CreateAudioFrame(AVCodecContext* encCtx, AVChannelLayout* layout, int sampleCount)
    {
        AVFrame* frame = ffmpeg.av_frame_alloc();
        if (frame == null)
        {
            throw new OutOfMemoryException("aac_alloc_frame");
        }

        try
        {
            frame->nb_samples = sampleCount;
            frame->format = (int)encCtx->sample_fmt;
            frame->sample_rate = encCtx->sample_rate;
            ThrowIfErr(ffmpeg.av_channel_layout_copy(&frame->ch_layout, layout), "aac_frame_layout");
            ThrowIfErr(ffmpeg.av_frame_get_buffer(frame, 0), "aac_frame_buffer");
            ThrowIfErr(ffmpeg.av_frame_make_writable(frame), "aac_frame_writable");
            return frame;
        }
        catch
        {
            ffmpeg.av_frame_free(&frame);
            throw;
        }
    }

    private static unsafe void WriteSamplesToFrame(
        AVFrame* frame,
        AVSampleFormat sampleFormat,
        float[] interleavedSamples,
        int samplesPerChannel,
        int channels)
    {
        int totalSamples = samplesPerChannel * channels;
        fixed (float* src = interleavedSamples)
        {
            switch (sampleFormat)
            {
                case AVSampleFormat.AV_SAMPLE_FMT_FLT:
                    Buffer.MemoryCopy(src, frame->data[0], totalSamples * sizeof(float), totalSamples * sizeof(float));
                    break;

                case AVSampleFormat.AV_SAMPLE_FMT_FLTP:
                    for (int channel = 0; channel < channels; channel++)
                    {
                        float* dst = (float*)frame->extended_data[channel];
                        for (int sample = 0; sample < samplesPerChannel; sample++)
                        {
                            dst[sample] = src[(sample * channels) + channel];
                        }
                    }
                    break;

                case AVSampleFormat.AV_SAMPLE_FMT_S16:
                    {
                        short* dst = (short*)frame->data[0];
                        for (int i = 0; i < totalSamples; i++)
                        {
                            dst[i] = FloatToPcm16(src[i]);
                        }
                        break;
                    }

                case AVSampleFormat.AV_SAMPLE_FMT_S16P:
                    for (int channel = 0; channel < channels; channel++)
                    {
                        short* dst = (short*)frame->extended_data[channel];
                        for (int sample = 0; sample < samplesPerChannel; sample++)
                        {
                            dst[sample] = FloatToPcm16(src[(sample * channels) + channel]);
                        }
                    }
                    break;

                default:
                    throw new NotSupportedException($"Unsupported AAC encoder sample format: {sampleFormat}");
            }
        }
    }

    private static short FloatToPcm16(float sample)
    {
        float clamped = Math.Clamp(sample, -1f, 1f);
        return (short)Math.Round(clamped * short.MaxValue);
    }

    private static unsafe void WriteEncodedPackets(
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

            ThrowIfErr(gp, "aac_receive_packet");
            ffmpeg.av_packet_rescale_ts(outPkt, encCtx->time_base, outStream->time_base);
            outPkt->stream_index = outStream->index;
            ThrowIfErr(ffmpeg.av_interleaved_write_frame(outFmt, outPkt), "aac_write_frame");
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

    #pragma warning disable CS0618
    private static unsafe AVSampleFormat ChooseSampleFormat(AVCodec* codec)
    {
        if (codec->sample_fmts == null)
        {
            return AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        }

        AVSampleFormat[] preferredFormats =
        [
            AVSampleFormat.AV_SAMPLE_FMT_FLTP,
            AVSampleFormat.AV_SAMPLE_FMT_FLT,
            AVSampleFormat.AV_SAMPLE_FMT_S16P,
            AVSampleFormat.AV_SAMPLE_FMT_S16
        ];

        for (AVSampleFormat* fmt = codec->sample_fmts; *fmt != AVSampleFormat.AV_SAMPLE_FMT_NONE; fmt++)
        {
            for (int i = 0; i < preferredFormats.Length; i++)
            {
                if (*fmt == preferredFormats[i])
                {
                    return preferredFormats[i];
                }
            }
        }

        return codec->sample_fmts[0];
    }
    #pragma warning restore CS0618
}
