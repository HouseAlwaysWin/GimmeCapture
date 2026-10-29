using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// Writes synthetic video files for the libav exporter and transcoder tests, and reads back the timing a video file
/// holds. YUV4MPEG2 needs no encoder and states its frame rate exactly (30000/1001 included); a file encoded here with
/// libx264 carries whatever timestamps a test hands it, so it can have a variable frame rate or a gap. Needs only the
/// bundled FFmpeg, which every CI job provisions.
/// </summary>
internal static class VideoTestClips
{
    /// <summary>The video stream of a file, as its packets give it.</summary>
    /// <param name="Frames">Video packets, one per frame.</param>
    /// <param name="FrameSeconds">Each frame's presentation time, in order.</param>
    /// <param name="EndSeconds">When the last frame stops showing: its time plus its duration.</param>
    /// <param name="FrameRate">The stream's average frame rate, as the demuxer reports it.</param>
    internal sealed record Timeline(int Frames, IReadOnlyList<double> FrameSeconds, double EndSeconds, AVRational FrameRate)
    {
        public double StartSeconds => FrameSeconds.Count > 0 ? FrameSeconds[0] : 0;

        public double DurationSeconds => EndSeconds - StartSeconds;
    }

    /// <summary>Writes a constant-rate raw clip of <paramref name="frameCount"/> frames at rateNum/rateDen fps.</summary>
    public static string WriteY4m(string path, int width, int height, int frameCount, int rateNum, int rateDen)
    {
        using FileStream file = File.Create(path);
        file.Write(Encoding.ASCII.GetBytes($"YUV4MPEG2 W{width} H{height} F{rateNum}:{rateDen} Ip A1:1 C420jpeg\n"));

        int lumaSize = width * height;
        int chromaSize = (width / 2) * (height / 2);
        var planes = new byte[lumaSize + (2 * chromaSize)];
        planes.AsSpan(lumaSize).Fill(128);
        byte[] frameHeader = Encoding.ASCII.GetBytes("FRAME\n");
        for (int i = 0; i < frameCount; i++)
        {
            // A grey that changes with every frame, so no two neighbours are alike.
            planes.AsSpan(0, lumaSize).Fill((byte)(16 + ((i * 7) % 220)));
            file.Write(frameHeader);
            file.Write(planes);
        }

        return path;
    }

    /// <summary>
    /// Encodes one frame per entry of <paramref name="pts"/> (in <paramref name="timeBase"/>) with libx264 into a file
    /// whose container follows the extension (.mkv, .mp4). Each frame lasts until the next one; the last lasts
    /// <paramref name="lastFrameDuration"/>. With <paramref name="detailed"/> the frames carry a moving, textured
    /// picture that costs an encoder real work, for timing encodes; otherwise a flat grey per frame.
    /// </summary>
    public static unsafe string WriteTimestamped(
        string path,
        int width,
        int height,
        AVRational timeBase,
        IReadOnlyList<long> pts,
        long lastFrameDuration,
        bool detailed = false,
        AVRational? frameRate = null)
    {
        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* output = null;
        AVCodecContext* encoder = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        AVDictionary* options = null;
        try
        {
            Check(ffmpeg.avformat_alloc_output_context2(&output, null, null, path), "alloc_output");
            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name("libx264");
            Assert.True(codec != null, "libx264 is not in the bundled FFmpeg");

            encoder = ffmpeg.avcodec_alloc_context3(codec);
            encoder->width = width;
            encoder->height = height;
            encoder->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            encoder->time_base = timeBase;
            if (frameRate is { } rate)
            {
                encoder->framerate = rate;
            }

            encoder->max_b_frames = 0;
            encoder->thread_count = 0;
            if ((output->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            {
                encoder->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            ffmpeg.av_dict_set(&options, "preset", "ultrafast", 0);
            ffmpeg.av_dict_set(&options, "crf", detailed ? "16" : "23", 0);
            Check(ffmpeg.avcodec_open2(encoder, codec, &options), "open_encoder");

            AVStream* stream = ffmpeg.avformat_new_stream(output, null);
            Check(ffmpeg.avcodec_parameters_from_context(stream->codecpar, encoder), "parameters_from_context");
            stream->time_base = timeBase;
            if (frameRate is { } streamRate)
            {
                stream->avg_frame_rate = streamRate;
            }

            Check(ffmpeg.avio_open(&output->pb, path, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
            Check(ffmpeg.avformat_write_header(output, null), "write_header");

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            frame->width = width;
            frame->height = height;
            Check(ffmpeg.av_frame_get_buffer(frame, 32), "frame_get_buffer");

            // Each packet carries its frame's duration, so the container records how long the last frame shows.
            var durations = new Dictionary<long, long>();

            for (int i = 0; i < pts.Count; i++)
            {
                Check(ffmpeg.av_frame_make_writable(frame), "make_writable");
                if (detailed)
                {
                    FillDetailed(frame, i);
                }
                else
                {
                    FillFlat(frame, i);
                }

                frame->pts = pts[i];
                frame->duration = i + 1 < pts.Count ? pts[i + 1] - pts[i] : lastFrameDuration;
                durations[pts[i]] = frame->duration;
                Check(ffmpeg.avcodec_send_frame(encoder, frame), "send_frame");
                WritePackets(encoder, output, stream, packet, durations);
            }

            Check(ffmpeg.avcodec_send_frame(encoder, null), "flush");
            WritePackets(encoder, output, stream, packet, durations);
            Check(ffmpeg.av_write_trailer(output), "write_trailer");
            return path;
        }
        finally
        {
            if (options != null) ffmpeg.av_dict_free(&options);
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (encoder != null) ffmpeg.avcodec_free_context(&encoder);
            if (output != null)
            {
                if (output->pb != null) ffmpeg.avio_closep(&output->pb);
                ffmpeg.avformat_free_context(output);
            }
        }
    }

    /// <summary>A constant-rate clip of <paramref name="frameCount"/> frames at <paramref name="fps"/>, as
    /// <see cref="WriteTimestamped"/> writes it.</summary>
    public static string WriteConstantRate(string path, int width, int height, int frameCount, int fps, bool detailed = false) =>
        WriteTimestamped(
            path, width, height, new AVRational { num = 1, den = fps }, Enumerable.Range(0, frameCount).Select(i => (long)i).ToArray(),
            lastFrameDuration: 1, detailed, new AVRational { num = fps, den = 1 });

    /// <summary>Reads every video packet's timing from <paramref name="path"/> without decoding.</summary>
    public static unsafe Timeline Read(string path)
    {
        FFmpegRuntime.EnsureInitialized();

        AVFormatContext* input = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            Check(ffmpeg.avformat_open_input(&input, path, null, null), "open_input");
            Check(ffmpeg.avformat_find_stream_info(input, null), "find_stream_info");
            int video = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            Assert.True(video >= 0, "no video stream");
            AVStream* stream = input->streams[video];
            double timeBase = ffmpeg.av_q2d(stream->time_base);

            var seconds = new List<double>();
            double end = 0;
            while (ffmpeg.av_read_frame(input, packet) >= 0)
            {
                if (packet->stream_index == video)
                {
                    long ts = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
                    double start = ts * timeBase;
                    seconds.Add(start);
                    end = Math.Max(end, start + (packet->duration * timeBase));
                }

                ffmpeg.av_packet_unref(packet);
            }

            seconds.Sort();
            return new Timeline(seconds.Count, seconds, end, stream->avg_frame_rate);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            if (input != null) ffmpeg.avformat_close_input(&input);
        }
    }

    private static unsafe void WritePackets(
        AVCodecContext* encoder, AVFormatContext* output, AVStream* stream, AVPacket* packet, Dictionary<long, long> durations)
    {
        while (ffmpeg.avcodec_receive_packet(encoder, packet) >= 0)
        {
            if (packet->duration <= 0 && durations.TryGetValue(packet->pts, out long duration))
            {
                packet->duration = duration;
            }

            ffmpeg.av_packet_rescale_ts(packet, encoder->time_base, stream->time_base);
            packet->stream_index = stream->index;
            Check(ffmpeg.av_interleaved_write_frame(output, packet), "write_frame");
        }
    }

    private static unsafe void FillFlat(AVFrame* frame, int index)
    {
        byte grey = (byte)(16 + ((index * 7) % 220));
        for (int row = 0; row < frame->height; row++)
        {
            new Span<byte>(frame->data[0] + (row * frame->linesize[0]), frame->width).Fill(grey);
        }

        for (int plane = 1; plane <= 2; plane++)
        {
            for (int row = 0; row < frame->height / 2; row++)
            {
                new Span<byte>(frame->data[(uint)plane] + (row * frame->linesize[(uint)plane]), frame->width / 2).Fill(128);
            }
        }
    }

    // A scrolling diagonal ramp, a block of fine "text" detail sliding across it and a little grain: roughly what a
    // screen recording with some motion asks of an encoder, rather than flat colour it encodes for free.
    private static unsafe void FillDetailed(AVFrame* frame, int index)
    {
        int width = frame->width;
        int height = frame->height;
        int blockWidth = width / 4;
        int blockHeight = height / 4;
        int blockLeft = (index * 12) % Math.Max(1, width - blockWidth);
        int blockTop = height / 3;

        for (int row = 0; row < height; row++)
        {
            byte* line = frame->data[0] + (row * frame->linesize[0]);
            bool inBlockRows = row >= blockTop && row < blockTop + blockHeight;
            for (int col = 0; col < width; col++)
            {
                int value;
                if (inBlockRows && col >= blockLeft && col < blockLeft + blockWidth)
                {
                    int cellX = (col - blockLeft) >> 3;
                    int cellY = (row - blockTop) >> 3;
                    value = (Hash(cellX, cellY, 0) & 1) == 0 ? 40 : 220;
                }
                else
                {
                    value = 32 + ((col + row + (index * 4)) % 192);
                }

                value += (Hash(col, row, index) & 3) - 1;
                line[col] = (byte)Math.Clamp(value, 0, 255);
            }
        }

        for (int row = 0; row < height / 2; row++)
        {
            byte* u = frame->data[1] + (row * frame->linesize[1]);
            byte* v = frame->data[2] + (row * frame->linesize[2]);
            for (int col = 0; col < width / 2; col++)
            {
                u[col] = (byte)(96 + (((col >> 3) + index) & 63));
                v[col] = (byte)(96 + (((row >> 3) + (index * 2)) & 63));
            }
        }
    }

    private static int Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)((x * 73856093) ^ (y * 19349663) ^ (z * 83492791));
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return (int)(h & 0x7fffffff);
        }
    }

    private static void Check(int result, string what)
    {
        Assert.True(result >= 0, $"{what} failed: {FFmpegErrors.Describe(result)} ({result})");
    }
}
