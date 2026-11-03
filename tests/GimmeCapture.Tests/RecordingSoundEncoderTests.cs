using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using NAudio.Wave;

namespace GimmeCapture.Tests;

/// <summary>
/// <see cref="RecordingSoundEncoder"/> encodes a recording's sound while the captures write its WAVs, so the stop does
/// not. Real libav on WAVs a test writes a burst at a time (<see cref="GrowingWav"/>), so it runs everywhere. What it
/// encodes against what the finalize would build is in <see cref="RecordingFinalizeTests"/>.
/// </summary>
public sealed class RecordingSoundEncoderTests : IDisposable
{
    private static readonly WaveFormat Stereo48 = new(48_000, 16, 2);
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(20);
    private static readonly long BitRate = LibavAacTranscoder.BitRate(VideoQuality.Medium);

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // %TEMP% clean-up gets it later
        }
    }

    [Fact]
    public async Task ATrackEncodedWhileItIsWritten_IsTheTrackEncodedAfterwards()
    {
        string wav = Path.Combine(_dir, "segment_0.wav");
        string recorded = Path.Combine(_dir, "recorded.m4a");
        using var capture = new GrowingWav(wav, Stereo48, seconds: 2, seed: 1);
        capture.WriteBurst(); // the encoder starts after the first frame, when the capture is writing already
        using var encoder = RecordingSoundEncoder.Start(recorded, wav, microphoneSegment: null, 1.0, BitRate);
        GrowingWav.WriteTogether(Pause, capture);
        capture.Dispose();
        encoder.EndSegment(RecordedSoundTrack.System);

        var result = await encoder.FinishAsync();

        string afterwards = Path.Combine(_dir, "afterwards.m4a");
        LibavAacTranscoder.EncodeWavToM4a(wav, afterwards, VideoQuality.Medium);
        Assert.Null(result.Failure);
        Assert.Equal(recorded, result.Path);
        Assert.Equal([wav], result.SystemSegments);
        Assert.Empty(result.MicrophoneSegments);
        Assert.Equal(LibavPinAudioPcmDecoder.Decode(afterwards, 0).PcmBytes, LibavPinAudioPcmDecoder.Decode(recorded, 0).PcmBytes);
    }

    [Fact]
    public async Task TheEncode_KeepsUpWithTheCapture()
    {
        // Twenty seconds of sound, which the encoder takes about half a second for: before the capture closes, most of the
        // AAC is in the file already. 160 KB is half of it, at 128 kbps.
        string wav = Path.Combine(_dir, "segment_0.wav");
        string recorded = Path.Combine(_dir, "recorded.m4a");
        using var capture = new GrowingWav(wav, Stereo48, seconds: 20, seed: 2);
        capture.WriteBurst();
        using var encoder = RecordingSoundEncoder.Start(recorded, wav, microphoneSegment: null, 1.0, BitRate);
        GrowingWav.WriteTogether(0.5, TimeSpan.FromMilliseconds(1), capture);

        var waited = Stopwatch.StartNew();
        while (Length(recorded) < 160_000 && waited.Elapsed < TimeSpan.FromSeconds(20))
        {
            await OwnThread.Delay(TimeSpan.FromMilliseconds(50));
        }

        long beforeTheStop = Length(recorded);
        capture.Dispose();
        encoder.EndSegment(RecordedSoundTrack.System);
        var result = await encoder.FinishAsync();

        Assert.True(beforeTheStop >= 160_000, $"Only {beforeTheStop} bytes of AAC were written before the stop.");
        Assert.Equal(20.0, LibavPinAudioPcmDecoder.Decode(result.Path!, 0).PcmBytes.Length / 192_000.0, 0.05);
    }

    [Fact]
    public async Task ASegmentOfATenthOfASecond_IsLeftOut_AsTheFinalizeLeavesItOut()
    {
        string[] segments = [Segment(0), Segment(1), Segment(2)];
        double[] seconds = [1.0, 0.1, 1.0];
        string recorded = Path.Combine(_dir, "recorded.m4a");
        RecordingSoundEncoder? encoder = null;
        for (int i = 0; i < segments.Length; i++)
        {
            using var capture = new GrowingWav(segments[i], Stereo48, seconds[i], seed: i);
            if (i == 0)
            {
                capture.WriteBurst();
                encoder = RecordingSoundEncoder.Start(recorded, segments[0], microphoneSegment: null, 1.0, BitRate);
            }
            else
            {
                encoder!.AddSegment(RecordedSoundTrack.System, segments[i]);
            }

            GrowingWav.WriteTogether(Pause, capture);
            capture.Dispose();
            encoder!.EndSegment(RecordedSoundTrack.System);
        }

        using (encoder)
        {
            var result = await encoder!.FinishAsync();

            Assert.Equal([segments[0], segments[2]], result.SystemSegments);
            Assert.False(RecordingService.HoldsSound((long)(0.1 * Stereo48.AverageBytesPerSecond), Stereo48));
            Assert.Equal(2.0, LibavPinAudioPcmDecoder.Decode(result.Path!, 0).PcmBytes.Length / 192_000.0, 0.05);
        }
    }

    // The finalize resamples a segment whose format changed across a pause; this encode leaves that to it.
    [Fact]
    public async Task ASegmentInAnotherFormat_EndsTheEncode_AndDeletesWhatItWrote()
    {
        string recorded = Path.Combine(_dir, "recorded.m4a");
        RecordingSoundEncoder encoder;
        using (var first = new GrowingWav(Segment(0), Stereo48, seconds: 1, seed: 1))
        {
            first.WriteBurst();
            encoder = RecordingSoundEncoder.Start(recorded, Segment(0), microphoneSegment: null, 1.0, BitRate);
            GrowingWav.WriteTogether(Pause, first);
        }

        using (encoder)
        {
            encoder.EndSegment(RecordedSoundTrack.System);
            encoder.AddSegment(RecordedSoundTrack.System, Segment(1));
            using (var second = new GrowingWav(Segment(1), new WaveFormat(44_100, 16, 2), seconds: 1, seed: 2))
            {
                GrowingWav.WriteTogether(Pause, second);
            }

            encoder.EndSegment(RecordedSoundTrack.System);
            var result = await encoder.FinishAsync();

            Assert.Null(result.Path);
            Assert.Contains("segment_1.wav", result.Failure);
            Assert.False(File.Exists(recorded));
        }
    }

    [Fact]
    public async Task Disposed_WhileItWaitsForTheCapture_ItStopsAndDeletesWhatItWrote()
    {
        string wav = Path.Combine(_dir, "segment_0.wav");
        string recorded = Path.Combine(_dir, "recorded.m4a");
        using var capture = new GrowingWav(wav, Stereo48, seconds: 5, seed: 3);
        capture.WriteBurst();
        var encoder = RecordingSoundEncoder.Start(recorded, wav, microphoneSegment: null, 1.0, BitRate);
        GrowingWav.WriteTogether(0.5, TimeSpan.FromMilliseconds(1), capture);
        var waited = Stopwatch.StartNew();
        while (Length(recorded) == 0 && waited.Elapsed < TimeSpan.FromSeconds(20))
        {
            await OwnThread.Delay(TimeSpan.FromMilliseconds(20));
        }

        var disposing = Stopwatch.StartNew();
        encoder.Dispose();
        TimeSpan took = disposing.Elapsed;
        var result = await encoder.FinishAsync();

        Assert.True(took < TimeSpan.FromSeconds(1), $"Dispose took {took.TotalMilliseconds:F0} ms.");
        Assert.Null(result.Path);
        Assert.False(File.Exists(recorded));
    }

    private string Segment(int index) => Path.Combine(_dir, $"segment_{index}.wav");

    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
}
