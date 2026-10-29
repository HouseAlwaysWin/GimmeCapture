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
