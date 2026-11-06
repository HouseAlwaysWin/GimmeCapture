using System.Collections.Generic;
using System.IO;
using FFmpeg.AutoGen;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// A pause or stop ends each segment's audio at the segment's <see cref="RecordingSegmentWriter.VideoLength"/>, and
/// finalize joins the segments end to end, so the next segment's video has to start exactly there. Encodes with libx264
/// from the bundled FFmpeg, which every CI job provisions; runs everywhere.
/// </summary>
public sealed class RecordingSegmentWriterTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "SegmentWriter", Guid.NewGuid().ToString("N"));

    public RecordingSegmentWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // %TEMP% clean-up gets it later.
        }
    }

    [Fact]
    public void VideoLength_RunsToTheLastStampPlusOneFrame()
    {
        // Wall-clock stamps skip a number when the capture falls behind.
        Assert.Equal(TimeSpan.FromSeconds(6 / 30.0), WriteSegment("segment.mkv", 0, 1, 2, 5));
    }

    [Fact]
    public void Finalize_StartsTheNextSegment_AtTheVideoLength()
    {
        TimeSpan firstLength = WriteSegment("first.mkv", 0, 1, 2, 5);
        WriteSegment("second.mkv", 0, 1, 2);
        string merged = Path.Combine(_folder, "merged.mkv");

        LibavMuxer.ConcatVideoSegments(
            [Path.Combine(_folder, "first.mkv"), Path.Combine(_folder, "second.mkv")], merged, "matroska");

        var starts = PacketStarts(merged);
        Assert.Equal(7, starts.Count);
        Assert.Equal(firstLength.TotalSeconds, starts[4], precision: 3); // the second segment's first frame
    }

    // A frame handed over as it is, shaped like gdigrab's (the bitmap decoder marks each an intra picture, key flag set):
    // an encoder takes those marks for an order to make the frame a keyframe, so every frame became one.
    [Fact]
    public unsafe void FramesEncodedAsIs_MakeNoKeyframesOfTheirOwn()
    {
        FFmpegRuntime.EnsureInitialized();
        string path = Path.Combine(_folder, "as-is.mkv");
        using (var writer = RecordingSegmentWriter.Open(path, VideoCodec.H264, preferHardware: false, width: 320, height: 240, fps: 30, crf: 0, bitrate: 0))
        {
            AVFrame* frame = DecodedLookingFrame(writer.Encoder);
            try
            {
                for (long pts = 0; pts < 20; pts++)
                {
                    frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
                    frame->flags = ffmpeg.AV_FRAME_FLAG_KEY;
                    writer.EncodeAsIs(frame, pts);
                }
            }
            finally
            {
                ffmpeg.av_frame_free(&frame);
            }

            writer.Flush();
            writer.WriteTrailer();
            Assert.Equal(20, writer.FrameCount);
        }

        // The writer's keyframe interval is two seconds, 60 frames: only the first of 20 is one.
        Assert.Equal(1, KeyPackets(path));
        Assert.Equal(20, PacketStarts(path).Count);
    }

    [Fact]
    public unsafe void ClearingAFramesSourceProperties_KeepsItsPicture()
    {
        FFmpegRuntime.EnsureInitialized();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            frame->width = 64;
            frame->height = 32;
            Assert.True(ffmpeg.av_frame_get_buffer(frame, 32) >= 0);
            byte* pixels = frame->data[0];
            frame->pts = 1234;
            frame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            frame->flags = ffmpeg.AV_FRAME_FLAG_KEY;
            frame->duration = 33_333;
            frame->time_base = new AVRational { num = 1, den = 1_000_000 };
            frame->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
            frame->colorspace = AVColorSpace.AVCOL_SPC_RGB;
            frame->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            frame->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            frame->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1;
            Assert.True(ffmpeg.av_frame_new_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX, 36) != null);

            RecordingSegmentWriter.ClearSourceProperties(frame);

            Assert.Equal((int)AVPixelFormat.AV_PIX_FMT_BGRA, frame->format);
            Assert.Equal((64, 32), (frame->width, frame->height));
            Assert.True(frame->data[0] == pixels);
            Assert.Equal(1234, frame->pts); // EncodeAsIs stamps it
            Assert.Equal(AVPictureType.AV_PICTURE_TYPE_NONE, frame->pict_type);
            Assert.Equal(0, frame->flags);
            Assert.Equal(0, frame->duration);
            Assert.Equal(0, frame->time_base.num);
            Assert.Equal(0, frame->sample_aspect_ratio.num);
            Assert.Equal(AVColorSpace.AVCOL_SPC_UNSPECIFIED, frame->colorspace);
            Assert.Equal(AVColorRange.AVCOL_RANGE_UNSPECIFIED, frame->color_range);
            Assert.Equal(AVColorPrimaries.AVCOL_PRI_UNSPECIFIED, frame->color_primaries);
            Assert.Equal(AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED, frame->color_trc);
            Assert.Equal(0, frame->nb_side_data);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    // A frame in the encoder's format and size with a picture of its own, as a grabber hands one over.
    private static unsafe AVFrame* DecodedLookingFrame(AVCodecContext* encoder)
    {
        AVFrame* frame = ffmpeg.av_frame_alloc();
        frame->format = (int)encoder->pix_fmt;
        frame->width = encoder->width;
        frame->height = encoder->height;
        Assert.True(ffmpeg.av_frame_get_buffer(frame, 32) >= 0);
        for (uint plane = 0; plane < 3; plane++)
        {
            int rows = plane == 0 ? frame->height : frame->height / 2;
            new Span<byte>(frame->data[plane], frame->linesize[plane] * rows).Fill(plane == 0 ? (byte)90 : (byte)128);
        }

        return frame;
    }

    private static unsafe int KeyPackets(string path)
    {
        AVFormatContext* fmt = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        try
        {
            Assert.True(ffmpeg.avformat_open_input(&fmt, path, null, null) >= 0, $"cannot open {path}");
            int keys = 0;
            while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
            {
                keys += (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0 ? 1 : 0;
                ffmpeg.av_packet_unref(pkt);
            }

            return keys;
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            if (fmt != null)
            {
                ffmpeg.avformat_close_input(&fmt);
            }
        }
    }

    private unsafe TimeSpan WriteSegment(string name, params long[] stamps)
    {
        FFmpegRuntime.EnsureInitialized();
        using var writer = RecordingSegmentWriter.Open(
            Path.Combine(_folder, name), VideoCodec.H264, preferHardware: false, width: 320, height: 240, fps: 30, crf: 0, bitrate: 0);
        foreach (long pts in stamps)
        {
            writer.FrameToFill();
            writer.EncodeFrame(pts);
        }

        writer.Flush();
        writer.WriteTrailer();
        return writer.VideoLength;
    }

    // Each video packet's stamp in seconds, in file order.
    private static unsafe List<double> PacketStarts(string path)
    {
        AVFormatContext* fmt = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        try
        {
            Assert.True(ffmpeg.avformat_open_input(&fmt, path, null, null) >= 0, $"cannot open {path}");
            Assert.True(ffmpeg.avformat_find_stream_info(fmt, null) >= 0, $"no stream info in {path}");
            var starts = new List<double>();
            while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
            {
                starts.Add(pkt->pts * ffmpeg.av_q2d(fmt->streams[pkt->stream_index]->time_base));
                ffmpeg.av_packet_unref(pkt);
            }

            return starts;
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            if (fmt != null)
            {
                ffmpeg.avformat_close_input(&fmt);
            }
        }
    }
}
