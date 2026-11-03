using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// Video segments encoded alike, joined with a sound into one file in a single pass (<see
/// cref="LibavMuxer.ConcatVideoSegmentsWithAudio"/>), against joining them into a Matroska file of their own and muxing
/// that with the sound, as the segmented compress did. Real libav on clips and a tone written in-process (<see
/// cref="VideoTestClips"/>); needs only the bundled FFmpeg.
/// </summary>
public sealed class LibavMuxerJoinTests : IDisposable
{
    private const int Fps = 30;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public LibavMuxerJoinTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    // Segments with no B-frames, whose first packet decodes when it shows, which the old way joined right.
    [Theory]
    [InlineData("out.mp4", "mp4")]
    [InlineData("out.mkv", "matroska")]
    public void InOnePass_TheSegmentsAndTheSound_AreWhatJoiningThenMuxingGave(string name, string container)
    {
        string[] segments = Segments(3, framesEach: 30);
        string sound = Sound(seconds: 3);
        string straight = Path.Combine(_dir, name);
        string throughAFile = Path.Combine(_dir, "two-pass-" + name);

        LibavMuxer.MuxStats joined = LibavMuxer.ConcatVideoSegmentsWithAudio(segments, sound, straight, container);
        string merged = Path.Combine(_dir, "merged.mkv");
        LibavMuxer.ConcatVideoSegments(segments, merged, "matroska");
        LibavMuxer.MuxStats muxed = LibavMuxer.MuxVideoAndAudio(merged, sound, throughAFile, container);

        Assert.Equal(muxed, joined);
        Assert.Equal(90, joined.VideoPackets);
        VideoTestClips.Timeline fromOnePass = VideoTestClips.Read(straight);
        VideoTestClips.Timeline fromTwo = VideoTestClips.Read(throughAFile);
        Assert.Equal(fromTwo.Frames, fromOnePass.Frames);
        // The file in between was Matroska, which keeps times to the millisecond.
        Assert.All(
            fromOnePass.FrameSeconds.Zip(fromTwo.FrameSeconds),
            pair => Assert.Equal(pair.Second, pair.First, 0.0015));
        Assert.Equal(fromTwo.DurationSeconds, fromOnePass.DurationSeconds, 0.0015);
        Assert.Equal(
            LibavPinAudioPcmDecoder.Decode(throughAFile, 0).PcmBytes.Length,
            LibavPinAudioPcmDecoder.Decode(straight, 0).PcmBytes.Length);
    }

    // Exported chunks, as the segmented compress makes them, have B-frames: each one's first packet decodes two frames
    // before it shows. Re-based on that decode time, as ConcatVideoSegments re-bases, every chunk after the first started
    // two frames after the one before it ended, so the joined video ran long of its sound by two frames a chunk.
    [Theory]
    [InlineData("out.mp4", "mp4")]
    [InlineData("out.mkv", "matroska")]
    public void ChunksWithBFrames_JoinEndToEnd(string name, string container)
    {
        string[] chunks = ExportedChunks(3);
        string sound = Sound(seconds: 3);
        string output = Path.Combine(_dir, name);

        LibavMuxer.ConcatVideoSegmentsWithAudio(chunks, sound, output, container);

        // Each chunk's frames as the chunk shows them, laid end to end.
        var expected = new List<double>();
        double offset = 0;
        foreach (string chunk in chunks)
        {
            VideoTestClips.Timeline own = VideoTestClips.Read(chunk);
            expected.AddRange(own.FrameSeconds.Select(seconds => seconds - own.StartSeconds + offset));
            offset += own.DurationSeconds;
        }

        VideoTestClips.Timeline joined = VideoTestClips.Read(output);
        Assert.Equal(expected.Count, joined.Frames);
        Assert.All(
            joined.FrameSeconds.Zip(expected),
            pair => Assert.Equal(pair.Second, pair.First, 0.0015));
        Assert.Equal(offset, joined.DurationSeconds, 0.0015);

        string merged = Path.Combine(_dir, "merged.mkv");
        LibavMuxer.ConcatVideoSegments(chunks, merged, "matroska");
        double oldWay = VideoTestClips.Read(merged).DurationSeconds;
        Assert.True(oldWay > offset + (2 * 0.03), $"the old join ran {oldWay:F3} s for {offset:F3} s of chunks");
    }

    [Fact]
    public void WithNoSound_TheSegmentsJoinOneAfterAnother()
    {
        string[] segments = Segments(3, framesEach: 30);
        string output = Path.Combine(_dir, "out.mp4");

        LibavMuxer.MuxStats joined = LibavMuxer.ConcatVideoSegmentsWithAudio(segments, audioPath: null, output, "mp4");

        Assert.Equal(new LibavMuxer.MuxStats(90, 0), joined);
        VideoTestClips.Timeline timeline = VideoTestClips.Read(output);
        Assert.Equal(90, timeline.Frames);
        Assert.All(
            timeline.FrameSeconds.Select((seconds, frame) => (seconds, frame)),
            f => Assert.Equal(f.frame / (double)Fps, f.seconds, 0.0015));
        Assert.Equal(3.0, timeline.DurationSeconds, 0.0015);
    }

    [Fact]
    public void ASegmentWithNoVideo_IsLeftOut()
    {
        string[] segments = Segments(2, framesEach: 30);
        string toneOnly = VideoTestClips.WriteTone(Path.Combine(_dir, "tone-only.wav"), 1.0);
        string output = Path.Combine(_dir, "out.mp4");

        LibavMuxer.MuxStats joined = LibavMuxer.ConcatVideoSegmentsWithAudio(
            [segments[0], toneOnly, segments[1]], audioPath: null, output, "mp4");

        Assert.Equal(60, joined.VideoPackets);
        Assert.Equal(2.0, VideoTestClips.Read(output).DurationSeconds, 0.0015);
    }

    /// <summary>
    /// The segmented compress's finalize both ways, in the order two passes, one, one, two: four 30 s chunks at
    /// 1280x720 and two minutes of AAC. Opt-in: JOIN_PROBE=1.
    /// </summary>
    [OptInFact("JOIN_PROBE=1")]
    public void JoiningInOnePass_Timed()
    {
        string[] segments = Enumerable.Range(0, 4)
            .Select(i => VideoTestClips.WriteConstantRate(
                Path.Combine(_dir, $"chunk_{i}.mp4"), 1280, 720, 30 * Fps, Fps, detailed: true, gopSize: 2 * Fps))
            .ToArray();
        string sound = Sound(seconds: 120);
        long chunkBytes = segments.Sum(path => new FileInfo(path).Length);

        foreach (bool onePass in new[] { false, true, true, false })
        {
            string output = Path.Combine(_dir, onePass ? "one.mp4" : "two.mp4");
            string merged = Path.Combine(_dir, "merged.mkv");
            var watch = Stopwatch.StartNew();
            if (onePass)
            {
                LibavMuxer.ConcatVideoSegmentsWithAudio(segments, sound, output, "mp4");
            }
            else
            {
                LibavMuxer.ConcatVideoSegments(segments, merged, "matroska");
                LibavMuxer.MuxVideoAndAudio(merged, sound, output, "mp4");
            }

            double seconds = watch.Elapsed.TotalSeconds;
            long written = new FileInfo(output).Length + (File.Exists(merged) ? new FileInfo(merged).Length : 0);
            _output.WriteLine(
                $"{(onePass ? "one pass  " : "two passes")}: {seconds:F2} s, {written / 1048576.0:F1} MB written " +
                $"for {chunkBytes / 1048576.0:F1} MB of chunks");
            File.Delete(output);
            File.Delete(merged);
        }
    }

    // One-second chunks of a clip with a tone, exported as the segmented compress exports its chunks.
    private string[] ExportedChunks(int count)
    {
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, count * Fps, Fps);
        return Enumerable.Range(0, count)
            .Select(i =>
            {
                string chunk = Path.Combine(_dir, $"chunk_{i}.mp4");
                Assert.True(LibavClipExporter.TryExport(
                    clip, [new LibavClipExporter.SourceRange(i, i + 1)], chunk, VideoQuality.Medium,
                    options: new LibavExportOptions { Preset = "ultrafast", DropAudio = true }));
                return chunk;
            })
            .ToArray();
    }

    private string[] Segments(int count, int framesEach) =>
        Enumerable.Range(0, count)
            .Select(i => VideoTestClips.WriteConstantRate(Path.Combine(_dir, $"segment_{i}.mp4"), 64, 48, framesEach, Fps))
            .ToArray();

    // A tone encoded as AAC, as an export's sound is.
    private string Sound(double seconds)
    {
        string wav = VideoTestClips.WriteTone(Path.Combine(_dir, "sound.wav"), seconds);
        string m4a = Path.Combine(_dir, "sound.m4a");
        LibavAacTranscoder.EncodeWavToM4a(wav, m4a, VideoQuality.Medium);
        return m4a;
    }
}
