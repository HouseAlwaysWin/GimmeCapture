using System;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// In-process (native libav avfilter, no ffmpeg.exe) pitch-preserving time-stretch for a chunk of
/// interleaved S16 PCM. Builds an <c>abuffer → atempo[ → atempo…] → abuffersink</c> graph so a
/// sped-up / slowed segment keeps natural pitch (unlike a plain resample). Used by
/// <see cref="LibavClipExporter"/> to retime per-segment audio to match per-segment video speed.
///
/// tempo &gt; 1 speeds up (shortens), &lt; 1 slows down (lengthens) — matching the video speed factor.
/// atempo only accepts 0.5–2.0 per instance, so factors outside that range are chained.
/// </summary>
internal static class LibavAtempoFilter
{
    /// <summary>
    /// Time-stretches <paramref name="pcmS16"/> (interleaved 16-bit PCM) by <paramref name="tempo"/>,
    /// returning new interleaved S16 PCM at the same sample rate / channel count. tempo == 1 returns the
    /// input unchanged.
    /// </summary>
    public static unsafe byte[] Process(byte[] pcmS16, int sampleRate, int channels, double tempo)
    {
        ArgumentNullException.ThrowIfNull(pcmS16);
        if (Math.Abs(tempo - 1.0) < 0.001 || pcmS16.Length == 0)
        {
            return pcmS16;
        }

        FFmpegRuntime.EnsureInitialized();

        AVFilterGraph* graph = ffmpeg.avfilter_graph_alloc();
        if (graph == null)
        {
            throw new OutOfMemoryException("atempo_alloc_graph");
        }

        AVFrame* inFrame = null;
        AVFrame* outFrame = null;
        AVChannelLayout layout = default;
        ffmpeg.av_channel_layout_default(&layout, channels);

        try
        {
            // Describe the channel layout (e.g. "stereo") for the abuffer args string.
            byte* layoutBuf = stackalloc byte[64];
            ffmpeg.av_channel_layout_describe(&layout, layoutBuf, 64);
            string layoutStr = Marshal.PtrToStringAnsi((IntPtr)layoutBuf) ?? (channels == 1 ? "mono" : "stereo");

            string srcArgs =
                $"time_base=1/{sampleRate}:sample_rate={sampleRate}:sample_fmt=s16:channel_layout={layoutStr}";

            AVFilter* abuffer = ffmpeg.avfilter_get_by_name("abuffer");
            AVFilter* atempo = ffmpeg.avfilter_get_by_name("atempo");
            AVFilter* abuffersink = ffmpeg.avfilter_get_by_name("abuffersink");
            if (abuffer == null || atempo == null || abuffersink == null)
            {
                throw new InvalidOperationException("atempo/abuffer filter unavailable in this libav build.");
            }

            AVFilterContext* srcCtx = null;
            ThrowIfErr(ffmpeg.avfilter_graph_create_filter(&srcCtx, abuffer, "in", srcArgs, null, graph), "atempo_src");

            // Chain one atempo per factor (each must be within 0.5..2.0).
            AVFilterContext* prev = srcCtx;
            int idx = 0;
            foreach (double factor in FactorChain(tempo))
            {
                AVFilterContext* tempoCtx = null;
                string args = "tempo=" + factor.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                ThrowIfErr(ffmpeg.avfilter_graph_create_filter(&tempoCtx, atempo, $"atempo{idx}", args, null, graph), "atempo_node");
                ThrowIfErr(ffmpeg.avfilter_link(prev, 0, tempoCtx, 0), "atempo_link");
                prev = tempoCtx;
                idx++;
            }

            AVFilterContext* sinkCtx = null;
            ThrowIfErr(ffmpeg.avfilter_graph_create_filter(&sinkCtx, abuffersink, "out", null, null, graph), "atempo_sink");
            ThrowIfErr(ffmpeg.avfilter_link(prev, 0, sinkCtx, 0), "atempo_link_sink");

            ThrowIfErr(ffmpeg.avfilter_graph_config(graph, null), "atempo_config");

            // Feed the whole chunk as one input frame, then flush.
            int bytesPerFrame = channels * 2; // S16
            int samplesPerChannel = pcmS16.Length / bytesPerFrame;

            inFrame = ffmpeg.av_frame_alloc();
            if (inFrame == null)
            {
                throw new OutOfMemoryException("atempo_alloc_in");
            }

            inFrame->nb_samples = samplesPerChannel;
            inFrame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
            inFrame->sample_rate = sampleRate;
            ThrowIfErr(ffmpeg.av_channel_layout_copy(&inFrame->ch_layout, &layout), "atempo_in_layout");
            ThrowIfErr(ffmpeg.av_frame_get_buffer(inFrame, 0), "atempo_in_buffer");
            ThrowIfErr(ffmpeg.av_frame_make_writable(inFrame), "atempo_in_writable");

            int copyBytes = samplesPerChannel * bytesPerFrame;
            fixed (byte* srcPcm = pcmS16)
            {
                Buffer.MemoryCopy(srcPcm, inFrame->data[0], copyBytes, copyBytes);
            }

            ThrowIfErr(ffmpeg.av_buffersrc_add_frame(srcCtx, inFrame), "atempo_push");
            ThrowIfErr(ffmpeg.av_buffersrc_add_frame(srcCtx, null), "atempo_flush"); // signal EOF

            using var outPcm = new MemoryStream();
            outFrame = ffmpeg.av_frame_alloc();
            if (outFrame == null)
            {
                throw new OutOfMemoryException("atempo_alloc_out");
            }

            while (true)
            {
                int got = ffmpeg.av_buffersink_get_frame(sinkCtx, outFrame);
                if (got == ffmpeg.AVERROR_EOF || got == ffmpeg.AVERROR(11)) // EAGAIN
                {
                    break;
                }
                ThrowIfErr(got, "atempo_pull");
                AppendFrameAsS16(outFrame, channels, outPcm);
                ffmpeg.av_frame_unref(outFrame);
            }

            return outPcm.ToArray();
        }
        finally
        {
            if (inFrame != null) ffmpeg.av_frame_free(&inFrame);
            if (outFrame != null) ffmpeg.av_frame_free(&outFrame);
            ffmpeg.avfilter_graph_free(&graph);
            ffmpeg.av_channel_layout_uninit(&layout);
        }
    }

    /// <summary>
    /// Splits a tempo factor into a chain each within atempo's supported 0.5–2.0 range
    /// (e.g. 4 → [2,2]; 0.25 → [0.5,0.5]; 1.5 → [1.5]).
    /// </summary>
    internal static System.Collections.Generic.IEnumerable<double> FactorChain(double tempo)
    {
        double remaining = tempo;
        while (remaining > 2.0 + 1e-9)
        {
            yield return 2.0;
            remaining /= 2.0;
        }
        while (remaining < 0.5 - 1e-9)
        {
            yield return 0.5;
            remaining /= 0.5;
        }
        yield return remaining;
    }

    private static unsafe void AppendFrameAsS16(AVFrame* frame, int channels, MemoryStream dst)
    {
        int n = frame->nb_samples;
        var fmt = (AVSampleFormat)frame->format;
        switch (fmt)
        {
            case AVSampleFormat.AV_SAMPLE_FMT_S16: // interleaved already
                {
                    int bytes = n * channels * 2;
                    var buf = new byte[bytes];
                    Marshal.Copy((IntPtr)frame->data[0], buf, 0, bytes);
                    dst.Write(buf, 0, bytes);
                    break;
                }
            case AVSampleFormat.AV_SAMPLE_FMT_S16P: // planar -> interleave
                {
                    var buf = new byte[n * channels * 2];
                    for (int ch = 0; ch < channels; ch++)
                    {
                        short* plane = (short*)frame->extended_data[ch];
                        for (int s = 0; s < n; s++)
                        {
                            short v = plane[s];
                            int o = ((s * channels) + ch) * 2;
                            buf[o] = (byte)(v & 0xFF);
                            buf[o + 1] = (byte)((v >> 8) & 0xFF);
                        }
                    }
                    dst.Write(buf, 0, buf.Length);
                    break;
                }
            case AVSampleFormat.AV_SAMPLE_FMT_FLT: // interleaved float -> S16
                {
                    var buf = new byte[n * channels * 2];
                    float* src = (float*)frame->data[0];
                    for (int i = 0; i < n * channels; i++)
                    {
                        WriteS16(buf, i * 2, src[i]);
                    }
                    dst.Write(buf, 0, buf.Length);
                    break;
                }
            case AVSampleFormat.AV_SAMPLE_FMT_FLTP: // planar float -> interleaved S16
                {
                    var buf = new byte[n * channels * 2];
                    for (int ch = 0; ch < channels; ch++)
                    {
                        float* plane = (float*)frame->extended_data[ch];
                        for (int s = 0; s < n; s++)
                        {
                            WriteS16(buf, ((s * channels) + ch) * 2, plane[s]);
                        }
                    }
                    dst.Write(buf, 0, buf.Length);
                    break;
                }
            default:
                throw new NotSupportedException($"Unexpected atempo output sample format: {fmt}");
        }
    }

    private static void WriteS16(byte[] buf, int offset, float sample)
    {
        float clamped = Math.Clamp(sample, -1f, 1f);
        short v = (short)Math.Round(clamped * short.MaxValue);
        buf[offset] = (byte)(v & 0xFF);
        buf[offset + 1] = (byte)((v >> 8) & 0xFF);
    }

    private static void ThrowIfErr(int err, string ctx)
    {
        if (err < 0)
        {
            throw FFmpegErrors.ToException(err, ctx);
        }
    }
}
