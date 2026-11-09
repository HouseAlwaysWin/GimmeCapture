using FFmpeg.AutoGen;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// A picture and a sound made into one file. A picture encoded with B-frames has frames decoded before the first one
/// shown, at negative times; the mux once took those for the start, which put the whole picture two frames after the
/// sound. Real libav on clips written in-process; needs only the bundled FFmpeg.
/// </summary>
public sealed class LibavMuxerMuxTests : IDisposable
{
    private const int Fps = 30;
    private const int FrameCount = 60;

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("out.mp4", "mp4")]
    [InlineData("out.mkv", "matroska")]
    public void APictureWithBFrames_StartsWithItsSound(string name, string container)
    {
        string source = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "source.mkv"), 128, 96, FrameCount, Fps, detailed: true);
        string video = Path.Combine(_dir, "video.mp4");
        Assert.True(LibavClipExporter.TryExport(
            source, [new LibavClipExporter.SourceRange(0, (double)FrameCount / Fps)], video, VideoQuality.Medium,
            options: new LibavExportOptions { DropAudio = true }));
        Assert.True(HasBFrames(video), "The encode gave no B-frames, so this checks nothing.");

        string m4a = Path.Combine(_dir, "sound.m4a");
        LibavAacTranscoder.EncodeWavToM4a(
            VideoTestClips.WriteTone(Path.Combine(_dir, "sound.wav"), (double)FrameCount / Fps), m4a, VideoQuality.Medium);

        string output = Path.Combine(_dir, name);
        LibavMuxer.MuxVideoAndAudio(video, m4a, output, container);

        (double picture, double sound) = FirstShownSeconds(output);
        Assert.Equal(sound, picture, 3);
        Assert.Equal(FrameCount, VideoTestClips.Read(output).Frames);
    }

    // Whether the first packet is decoded ahead of when it shows.
    private static unsafe bool HasBFrames(string path)
    {
        FFmpegRuntime.EnsureInitialized();
        AVFormatContext* input = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            Assert.True(ffmpeg.avformat_open_input(&input, path, null, null) >= 0);
            Assert.True(ffmpeg.avformat_find_stream_info(input, null) >= 0);
            Assert.True(ffmpeg.av_read_frame(input, packet) >= 0);
            return packet->dts != ffmpeg.AV_NOPTS_VALUE && packet->dts < packet->pts;
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            if (input != null)
            {
                ffmpeg.avformat_close_input(&input);
            }
        }
    }

    // When the first frame and the first sound are shown, as a player reads the file.
    private static unsafe (double Picture, double Sound) FirstShownSeconds(string path)
    {
        FFmpegRuntime.EnsureInitialized();
        AVFormatContext* input = null;
        AVPacket* packet = ffmpeg.av_packet_alloc();
        try
        {
            Assert.True(ffmpeg.avformat_open_input(&input, path, null, null) >= 0);
            Assert.True(ffmpeg.avformat_find_stream_info(input, null) >= 0);
            int video = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            int audio = ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            Assert.True(video >= 0 && audio >= 0);

            double picture = double.MaxValue, sound = double.MaxValue;
            while (ffmpeg.av_read_frame(input, packet) >= 0)
            {
                double seconds = packet->pts * ffmpeg.av_q2d(input->streams[packet->stream_index]->time_base);
                if (packet->stream_index == video)
                {
                    picture = Math.Min(picture, seconds);
                }
                else if (packet->stream_index == audio)
                {
                    sound = Math.Min(sound, seconds);
                }

                ffmpeg.av_packet_unref(packet);
            }

            return (picture, sound);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            if (input != null)
            {
                ffmpeg.avformat_close_input(&input);
            }
        }
    }
}
