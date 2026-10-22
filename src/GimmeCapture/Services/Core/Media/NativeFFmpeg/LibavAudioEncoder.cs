using System;
using FFmpeg.AutoGen;
using GimmeCapture.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// The WAV -> compressed-audio encode behind <see cref="LibavAacTranscoder"/>, <see cref="LibavMp3Transcoder"/> and
/// <see cref="LibavOpusTranscoder"/> (native libav*, no Media Foundation / no ffmpeg.exe): the WAV is read through
/// NAudio, resampled to 48 kHz as mono or stereo, encoded a frame at a time and muxed into one container.
/// </summary>
internal static class LibavAudioEncoder
{
    /// <summary>What differs between the codecs this encodes to.</summary>
    /// <param name="Container">The libav muxer, e.g. "mp4".</param>
    /// <param name="EncoderName">The preferred encoder, e.g. "libopus"; <paramref name="FallbackCodecId"/> is tried
    /// when the build lacks it.</param>
    /// <param name="FallbackSampleFormat">The sample format to use when the encoder lists none.</param>
    /// <param name="FallbackFrameSize">Samples per channel in a frame when the opened encoder reports no frame size.</param>
    /// <param name="ErrorPrefix">Prefixes every error context, so a failure names the codec.</param>
    internal readonly record struct Target(
        string Container,
        string EncoderName,
        AVCodecID FallbackCodecId,
        AVSampleFormat FallbackSampleFormat,
        int FallbackFrameSize,
        string ErrorPrefix);

    /// <summary>
    /// Bits per second: <paramref name="overrideKbps"/> when it is set, otherwise 192 kbps for High, 128 kbps for
    /// Medium and <paramref name="lowKbps"/> for Low.
    /// </summary>
    public static long BitRate(VideoQuality quality, int overrideKbps, int lowKbps) =>
        overrideKbps > 0
            ? overrideKbps * 1000L
            : quality switch
            {
                VideoQuality.High => 192_000,
                VideoQuality.Low => lowKbps * 1000L,
                _ => 128_000,
            };

    /// <param name="channels">1 mixes the input down to mono; 2 encodes stereo, folding more channels down.</param>
    public static unsafe void EncodeWav(string wavPath, string outPath, Target target, long bitRate, int channels)
    {
        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Only mono or stereo output is supported.");
        }

        FFmpegRuntime.EnsureInitialized();

        string ctx = target.ErrorPrefix;
        using var wavReader = new WaveFileReader(wavPath);
        ISampleProvider provider = CreateNormalizedSampleProvider(wavReader, channels);

        AVFormatContext* outFmt = null;
        AVCodecContext* encCtx = null;
        AVPacket* outPkt = null;
        AVStream* outStream = null;
        AVChannelLayout outLayout = default;
        ffmpeg.av_channel_layout_default(&outLayout, channels);

        try
        {
            ThrowIfErr(ffmpeg.avformat_alloc_output_context2(&outFmt, null, target.Container, outPath), $"{ctx}_alloc_out");
            if (outFmt == null)
            {
                throw new InvalidOperationException($"Failed to create {target.Container} output context.");
            }

            AVCodec* enc = ffmpeg.avcodec_find_encoder_by_name(target.EncoderName);
            if (enc == null)
            {
                enc = ffmpeg.avcodec_find_encoder(target.FallbackCodecId);
            }

            if (enc == null)
            {
                throw new InvalidOperationException($"{target.EncoderName} encoder unavailable.");
            }

            encCtx = ffmpeg.avcodec_alloc_context3(enc);
            if (encCtx == null)
            {
                throw new OutOfMemoryException($"{ctx}_alloc_enc");
            }

            encCtx->sample_rate = provider.WaveFormat.SampleRate;
            encCtx->time_base = new AVRational { num = 1, den = encCtx->sample_rate };
            ThrowIfErr(ffmpeg.av_channel_layout_copy(&encCtx->ch_layout, &outLayout), $"{ctx}_enc_layout");
            encCtx->sample_fmt = ChooseSampleFormat(enc, target.FallbackSampleFormat);
            encCtx->bit_rate = bitRate;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                encCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            ThrowIfErr(ffmpeg.avcodec_open2(encCtx, enc, null), $"{ctx}_open_enc");

            outStream = ffmpeg.avformat_new_stream(outFmt, null);
            if (outStream == null)
            {
                throw new OutOfMemoryException($"{ctx}_new_stream");
            }

            ThrowIfErr(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encCtx), $"{ctx}_par_from_ctx");
            outStream->codecpar->codec_tag = 0;
            outStream->time_base = encCtx->time_base;

            if ((outFmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                ThrowIfErr(ffmpeg.avio_open(&outFmt->pb, outPath, ffmpeg.AVIO_FLAG_WRITE), $"{ctx}_avio_open");
            }

            ThrowIfErr(ffmpeg.avformat_write_header(outFmt, null), $"{ctx}_write_header");

            outPkt = ffmpeg.av_packet_alloc();
            if (outPkt == null)
            {
                throw new OutOfMemoryException($"{ctx}_alloc_pkt");
            }

            // provider was normalised to `channels` (mono or stereo) by CreateNormalizedSampleProvider.
            int frameSamples = encCtx->frame_size > 0 ? encCtx->frame_size : target.FallbackFrameSize;
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

                AVFrame* frame = CreateAudioFrame(encCtx, &outLayout, frameSamples, ctx);
                try
                {
                    WriteSamplesToFrame(frame, encCtx->sample_fmt, sampleBuffer, frameSamples, channels, ctx);
                    frame->pts = ptsSamples;
                    ptsSamples += samplesReadPerChannel;
                    ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, frame), $"{ctx}_send_frame");
                    WriteEncodedPackets(encCtx, outFmt, outStream, outPkt, ctx);
                }
                finally
                {
                    if (frame != null)
                    {
                        ffmpeg.av_frame_free(&frame);
                    }
                }
            }

            ThrowIfErr(ffmpeg.avcodec_send_frame(encCtx, null), $"{ctx}_flush_enc");
            WriteEncodedPackets(encCtx, outFmt, outStream, outPkt, ctx);

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

            ffmpeg.av_channel_layout_uninit(&outLayout);
        }
    }

    private static ISampleProvider CreateNormalizedSampleProvider(WaveFileReader reader, int targetChannels)
    {
        ISampleProvider provider = reader.ToSampleProvider();

        provider = targetChannels == 1
            ? ToMono(provider)
            : provider.WaveFormat.Channels switch
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

    // Mixes any input down to a single channel (stereo→mono averages L/R; >2 is first folded to stereo).
    private static ISampleProvider ToMono(ISampleProvider provider)
    {
        if (provider.WaveFormat.Channels == 1)
        {
            return provider;
        }

        if (provider.WaveFormat.Channels > 2)
        {
            provider = DownmixToStereo(provider);
        }

        return new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };
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

    private static unsafe AVFrame* CreateAudioFrame(AVCodecContext* encCtx, AVChannelLayout* layout, int sampleCount, string ctx)
    {
        AVFrame* frame = ffmpeg.av_frame_alloc();
        if (frame == null)
        {
            throw new OutOfMemoryException($"{ctx}_alloc_frame");
        }

        try
        {
            frame->nb_samples = sampleCount;
            frame->format = (int)encCtx->sample_fmt;
            frame->sample_rate = encCtx->sample_rate;
            ThrowIfErr(ffmpeg.av_channel_layout_copy(&frame->ch_layout, layout), $"{ctx}_frame_layout");
            ThrowIfErr(ffmpeg.av_frame_get_buffer(frame, 0), $"{ctx}_frame_buffer");
            ThrowIfErr(ffmpeg.av_frame_make_writable(frame), $"{ctx}_frame_writable");
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
        int channels,
        string ctx)
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
                    throw new NotSupportedException($"Unsupported {ctx} encoder sample format: {sampleFormat}");
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
        AVPacket* outPkt,
        string ctx)
    {
        while (true)
        {
            int gp = ffmpeg.avcodec_receive_packet(encCtx, outPkt);
            if (gp == ffmpeg.AVERROR_EOF || gp == ffmpeg.AVERROR(11))
            {
                break;
            }

            ThrowIfErr(gp, $"{ctx}_receive_packet");
            ffmpeg.av_packet_rescale_ts(outPkt, encCtx->time_base, outStream->time_base);
            outPkt->stream_index = outStream->index;
            ThrowIfErr(ffmpeg.av_interleaved_write_frame(outFmt, outPkt), $"{ctx}_write_frame");
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

    // The first format the encoder lists that WriteSamplesToFrame can fill, in the encoder's own order.
    #pragma warning disable CS0618
    private static unsafe AVSampleFormat ChooseSampleFormat(AVCodec* codec, AVSampleFormat fallback)
    {
        if (codec->sample_fmts == null)
        {
            return fallback;
        }

        for (AVSampleFormat* fmt = codec->sample_fmts; *fmt != AVSampleFormat.AV_SAMPLE_FMT_NONE; fmt++)
        {
            if (*fmt is AVSampleFormat.AV_SAMPLE_FMT_FLTP or AVSampleFormat.AV_SAMPLE_FMT_FLT
                or AVSampleFormat.AV_SAMPLE_FMT_S16P or AVSampleFormat.AV_SAMPLE_FMT_S16)
            {
                return *fmt;
            }
        }

        return codec->sample_fmts[0];
    }
    #pragma warning restore CS0618
}
