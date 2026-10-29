using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using FFmpeg.AutoGen;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

// Measures audio/video alignment in a finished recording from the packets themselves, not the container's summary
// fields: where each stream's first packet starts and where its last one ends. A recording covers one stretch of
// wall-clock time, so both streams should start together and run the same length. Audio captured before the first
// video frame (or after the last) shows up here as a start offset and a length difference.
public sealed class RecordingAvSyncTests
{
    private const string FileVariable = "GIMMECAPTURE_AV_FILE";
    private readonly ITestOutputHelper _output;

    public RecordingAvSyncTests(ITestOutputHelper output) => _output = output;

    internal readonly record struct StreamExtent(AVMediaType Type, double StartSeconds, double EndSeconds)
    {
        public double LengthSeconds => EndSeconds - StartSeconds;
    }

    internal static unsafe List<StreamExtent> ProbeExtents(string path)
    {
        FFmpegRuntime.EnsureInitialized();
        AVFormatContext* fmt = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        try
        {
            if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0)
                throw new InvalidOperationException($"Cannot open {path}");
            if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                throw new InvalidOperationException($"No stream info in {path}");

            int count = (int)fmt->nb_streams;
            var first = new long[count];
            var last = new long[count];
            Array.Fill(first, long.MaxValue);
            Array.Fill(last, long.MinValue);

            while (ffmpeg.av_read_frame(fmt, pkt) >= 0)
            {
                int i = pkt->stream_index;
                long pts = pkt->pts != ffmpeg.AV_NOPTS_VALUE ? pkt->pts : pkt->dts;
                if (pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    first[i] = Math.Min(first[i], pts);
                    last[i] = Math.Max(last[i], pts + Math.Max(0, pkt->duration));
                }

                ffmpeg.av_packet_unref(pkt);
            }

            var extents = new List<StreamExtent>(count);
            for (int i = 0; i < count; i++)
            {
                if (first[i] == long.MaxValue) continue;
                AVStream* stream = fmt->streams[i];
                double timeBase = ffmpeg.av_q2d(stream->time_base);
                extents.Add(new StreamExtent(stream->codecpar->codec_type, first[i] * timeBase, last[i] * timeBase));
            }

            return extents;
        }
        finally
        {
            ffmpeg.av_packet_free(&pkt);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }

    [OptInFact(FileVariable)]
    public void AudioAndVideo_StartTogether_AndRunTheSameLength()
    {
        string path = Environment.GetEnvironmentVariable(FileVariable)!;
        var extents = ProbeExtents(path);
        foreach (var e in extents)
        {
            _output.WriteLine($"{e.Type,-22} start {e.StartSeconds,8:F3}s  end {e.EndSeconds,8:F3}s  length {e.LengthSeconds,8:F3}s");
        }

        var video = Assert.Single(extents, e => e.Type == AVMediaType.AVMEDIA_TYPE_VIDEO);
        var audio = Assert.Single(extents, e => e.Type == AVMediaType.AVMEDIA_TYPE_AUDIO);
        double startOffset = audio.StartSeconds - video.StartSeconds;
        double lengthDifference = audio.LengthSeconds - video.LengthSeconds;
        _output.WriteLine($"audio minus video: start {startOffset:+0.000;-0.000}s, length {lengthDifference:+0.000;-0.000}s");

        Assert.True(Math.Abs(startOffset) <= 0.1, $"Audio starts {startOffset:+0.000;-0.000}s from video.");
        Assert.True(Math.Abs(lengthDifference) <= 0.25, $"Audio runs {lengthDifference:+0.000;-0.000}s longer than video (audio {audio.LengthSeconds:F3}s, video {video.LengthSeconds:F3}s).");
    }

    // Records with system audio through the real RecordingService, then measures each muxed result the same way.
    // Driven from an STA thread that pumps messages, as the app's UI thread does, because that is where the overlay
    // calls StartAsync from. Opt-in like the other recording probes: it captures the real screen and needs an audio
    // endpoint. The first recording also loads FFmpeg, which is the slowest start there is.
    [OptInFact("AV_SYNC_PROBE=1")]
    public void Recording_WithSystemAudio_KeepsAudioAndVideoTheSameLength()
    {
        string root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", "AvSync", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var recorded = new List<string>();
            Exception? failure = null;
            var ui = new Thread(() =>
            {
                try
                {
                    System.Windows.Forms.Application.Run(new RecordContext(root, recorded, _output));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            Assert.True(ui.Join(TimeSpan.FromMinutes(3)), "the recording thread did not finish");
            Assert.Null(failure);
            Assert.Equal(RecordContext.Recordings, recorded.Count);

            var differences = new List<double>();
            foreach (string file in recorded)
            {
                var extents = ProbeExtents(file);
                var video = Assert.Single(extents, e => e.Type == AVMediaType.AVMEDIA_TYPE_VIDEO);
                var audio = Assert.Single(extents, e => e.Type == AVMediaType.AVMEDIA_TYPE_AUDIO);
                double difference = audio.LengthSeconds - video.LengthSeconds;
                differences.Add(difference);
                _output.WriteLine($"{Path.GetFileName(file)}: video {video.LengthSeconds:F3}s, audio {audio.LengthSeconds:F3}s, audio minus video {difference:+0.000;-0.000}s");
            }

            // In sync, both streams cover the same stretch of wall-clock time: what is left is an audio buffer, a video
            // frame and the AAC priming samples, all well under 0.1 s. Audio that started before the first frame or
            // ran on after the last one is what this catches.
            Assert.All(differences, d => Assert.True(Math.Abs(d) <= 0.1, $"Audio runs {d:+0.000;-0.000}s longer than video."));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* best effort */ }
        }
    }

    private sealed class RecordContext : System.Windows.Forms.ApplicationContext
    {
        public const int Recordings = 3;
        private const int RecordMilliseconds = 3000;

        private readonly string _root;
        private readonly List<string> _recorded;
        private readonly ITestOutputHelper _output;

        public RecordContext(string root, List<string> recorded, ITestOutputHelper output)
        {
            _root = root;
            _recorded = recorded;
            _output = output;

            // Start from inside the message loop, so the awaits below capture the pumping SynchronizationContext.
            var kick = new System.Windows.Forms.Timer { Interval = 50 };
            kick.Tick += (_, _) =>
            {
                kick.Stop();
                RunAsync();
            };
            kick.Start();
        }

        private async void RunAsync()
        {
            // No silence of the test's own: on a quiet machine WASAPI loopback hands over no packets while nothing renders,
            // and the recording keeps its system audio delivering itself, which this measures along with the rest.
            try
            {
                var settings = new AppSettingsService(_root);
                var service = new RecordingService(new FFmpegDownloaderService(settings), settings);
                for (int i = 0; i < Recordings; i++)
                {
                    string output = Path.Combine(_root, $"rec_{i}.mp4");
                    bool started = await service.StartAsync(
                        new Avalonia.Rect(100, 100, 1280, 720), output, "mp4",
                        includeCursor: true, fps: 30, recordSystemAudio: true, recordMicrophone: false);
                    if (!started)
                    {
                        _output.WriteLine($"recording {i} did not start: {service.LastStartError}");
                        continue;
                    }

                    _output.WriteLine($"recording {i}: {service.LastStartTimings}");
                    await Task.Delay(RecordMilliseconds);
                    await service.StopAsync(); // returns once the final mux is written

                    if (File.Exists(output))
                    {
                        _recorded.Add(output);
                    }
                    else
                    {
                        _output.WriteLine($"recording {i}: no {Path.GetFileName(output)}; root holds {string.Join(", ", Directory.GetFiles(_root))}");
                    }
                }
            }
            catch (Exception ex)
            {
                _output.WriteLine("recording failed: " + ex);
            }
            finally
            {
                ExitThread();
            }
        }
    }
}
