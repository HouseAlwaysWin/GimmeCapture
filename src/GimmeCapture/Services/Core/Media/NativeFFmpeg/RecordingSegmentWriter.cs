using System;
using FFmpeg.AutoGen;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Media.NativeFFmpeg;

/// <summary>
/// The encoder and the Matroska file of one recording segment, shared by the gdigrab, WGC and WGC-composite sessions:
/// it opens the recording encoder and the file, takes the frames a session produces, and flushes and closes both. A
/// session only decides where its frames come from, how they are paced and how they are stamped.
/// One thread at a time: the pipelined gdigrab path hands it to its encoder thread for the whole capture.
/// </summary>
internal sealed unsafe class RecordingSegmentWriter : IDisposable
{
    private AVFormatContext* _output;
    private AVCodecContext* _encoder;
    private AVStream* _stream;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private AVDictionary* _encoderOptions;
    private long _packets;
    private long _lastPts = -1;
    private AVRational _timeBase;

    private RecordingSegmentWriter()
    {
    }

    /// <summary>The encoder the ladder settled on, e.g. "h264_nvenc" or "libx264".</summary>
    public string EncoderName { get; private set; } = string.Empty;

    /// <summary>Why the encoder is not the one asked for, when it is not.</summary>
    public string? EncoderWarning { get; private set; }

    /// <summary>How long the encoder ladder took to open an encoder, failed candidates included.</summary>
    public TimeSpan EncoderOpenTime { get; private set; }

    /// <summary>The opened encoder. Frames must be in its pixel format and size; its time base is 1/fps.</summary>
    public AVCodecContext* Encoder => _encoder;

    /// <summary>Frames sent to the encoder so far.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Packets written to the file so far.</summary>
    public long PacketCount => _packets;

    /// <summary>
    /// How long the segment's video runs: to its last frame's stamp plus one frame, which is where finalize starts the
    /// next segment when it joins them.
    /// </summary>
    public TimeSpan VideoLength =>
        _lastPts < 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((_lastPts + 1) * ffmpeg.av_q2d(_timeBase));

    /// <summary>
    /// Opens the recording encoder for <paramref name="width"/> x <paramref name="height"/> at <paramref name="fps"/>
    /// (<see cref="LibavRecordingEncoder.OpenRecordingEncoderContext"/>), creates the Matroska file and writes its header.
    /// </summary>
    public static RecordingSegmentWriter Open(
        string outputPath, VideoCodec codec, bool preferHardware, int width, int height, int fps, int crf, long bitrate)
    {
        var writer = new RecordingSegmentWriter();
        try
        {
            writer.OpenCore(outputPath, codec, preferHardware, width, height, fps, crf, bitrate);
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private void OpenCore(
        string outputPath, VideoCodec codec, bool preferHardware, int width, int height, int fps, int crf, long bitrate)
    {
        _packet = ffmpeg.av_packet_alloc();

        AVDictionary* options = null;
        long opening = System.Diagnostics.Stopwatch.GetTimestamp();
        _encoder = LibavRecordingEncoder.OpenRecordingEncoderContext(
            codec, preferHardware, width, height, fps, crf, bitrate, &options,
            out string encoderName, out string? warning);
        EncoderOpenTime = System.Diagnostics.Stopwatch.GetElapsedTime(opening);
        _encoderOptions = options;
        if (_encoder == null)
        {
            throw new InvalidOperationException("No compatible video encoder available for recording.");
        }

        EncoderName = encoderName;
        EncoderWarning = warning;
        _timeBase = _encoder->time_base;

        AVFormatContext* output = null;
        int allocated = ffmpeg.avformat_alloc_output_context2(&output, null, "matroska", outputPath);
        _output = output;
        ThrowIfErr(allocated, "avformat_alloc_output_context2");

        _stream = ffmpeg.avformat_new_stream(_output, null);
        if (_stream == null)
        {
            throw new OutOfMemoryException("new_stream");
        }

        ThrowIfErr(ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _encoder), "codec_parameters_from_context");
        _stream->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        _stream->codecpar->codec_id = _encoder->codec_id;
        _stream->time_base = _encoder->time_base;
        _stream->avg_frame_rate = _encoder->framerate;
        _stream->r_frame_rate = _encoder->framerate;
        _stream->codecpar->codec_tag = 0;

        if ((_output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
        {
            ThrowIfErr(ffmpeg.avio_open(&_output->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
        }

        ThrowIfErr(ffmpeg.avformat_write_header(_output, null), "write_header");
    }

    /// <summary>
    /// The encoder-format frame to fill for the next <see cref="EncodeFrame"/>: allocated on first use, made writable
    /// every time.
    /// </summary>
    public AVFrame* FrameToFill()
    {
        if (_frame == null)
        {
            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)_encoder->pix_fmt;
            _frame->width = _encoder->width;
            _frame->height = _encoder->height;
            ThrowIfErr(ffmpeg.av_frame_get_buffer(_frame, 32), "frame_get_buffer(enc)");
        }

        ThrowIfErr(ffmpeg.av_frame_make_writable(_frame), "frame_make_writable(enc)");
        return _frame;
    }

    /// <summary>Encodes the frame <see cref="FrameToFill"/> returned, stamped with <paramref name="pts"/> (in 1/fps).</summary>
    public void EncodeFrame(long pts)
    {
        _frame->pts = pts;
        _lastPts = Math.Max(_lastPts, pts);
        FrameCount++;
        ThrowIfErr(ffmpeg.avcodec_send_frame(_encoder, _frame), "send_frame(enc)");
        LibavRecordingEncoder.WriteEncodedPackets(_encoder, _output, _stream, _packet, ref _packets);
    }

    /// <summary>Encodes a frame the caller allocated and stamped, then frees it.</summary>
    public void EncodeAndFree(AVFrame* frame, string context)
    {
        _lastPts = Math.Max(_lastPts, frame->pts);
        int sent = ffmpeg.avcodec_send_frame(_encoder, frame);
        ffmpeg.av_frame_free(&frame);
        ThrowIfErr(sent, context);
        FrameCount++;
        LibavRecordingEncoder.WriteEncodedPackets(_encoder, _output, _stream, _packet, ref _packets);
    }

    /// <summary>Drains the encoder into the file once the last frame is in.</summary>
    public void Flush(string context = "flush_encoder")
    {
        ThrowIfErr(ffmpeg.avcodec_send_frame(_encoder, null), context);
        LibavRecordingEncoder.WriteEncodedPackets(_encoder, _output, _stream, _packet, ref _packets);
    }

    /// <summary>Completes the file, after <see cref="Flush"/>.</summary>
    public void WriteTrailer() => ffmpeg.av_write_trailer(_output);

    public void Dispose()
    {
        if (_packet != null)
        {
            AVPacket* packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }

        if (_frame != null)
        {
            AVFrame* frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_encoder != null)
        {
            AVCodecContext* encoder = _encoder;
            ffmpeg.avcodec_free_context(&encoder);
            _encoder = null;
        }

        if (_output != null)
        {
            if ((_output->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && _output->pb != null)
            {
                ffmpeg.avio_closep(&_output->pb);
            }

            ffmpeg.avformat_free_context(_output);
            _output = null;
        }

        if (_encoderOptions != null)
        {
            AVDictionary* options = _encoderOptions;
            ffmpeg.av_dict_free(&options);
            _encoderOptions = null;
        }
    }

    private static void ThrowIfErr(int err, string ctx) => LibavRecordingEncoder.ThrowIfErr(err, ctx);
}

/// <summary>Paces a constant-rate recording loop: the WGC sessions emit the latest captured picture once per 1/fps.</summary>
internal static class RecordingFramePacer
{
    /// <summary>
    /// Whether frame number <paramref name="frameCount"/> is due on <paramref name="clock"/>. When it is not, sleeps
    /// toward its due time, at most 16 ms so a stop request is seen quickly, and returns false.
    /// </summary>
    public static bool IsFrameDue(System.Diagnostics.Stopwatch clock, long frameCount, int fps)
    {
        long target = (long)(clock.Elapsed.TotalSeconds * fps);
        if (frameCount <= target)
        {
            return true;
        }

        double nextDueMs = frameCount * 1000.0 / fps;
        double sleepMs = nextDueMs - clock.Elapsed.TotalMilliseconds;
        System.Threading.Thread.Sleep(sleepMs > 1 ? (int)Math.Min(sleepMs, 16) : 1);
        return false;
    }
}
