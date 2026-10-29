using System.Diagnostics;
using System.IO;
using GimmeCapture.Services.Core.Media;
using NAudio.Utils;
using NAudio.Wave;

namespace GimmeCapture.Tests;

/// <summary>
/// The audio gate keeps only the audio captured between a segment's first frame and its stop, cutting the 50-70 ms
/// bursts a capture hands over at those edges. A burst is placed by when it arrived, as the audio that ended then. Times
/// here are Stopwatch timestamps a fixed way from an arbitrary origin, written in milliseconds.
/// </summary>
public class RecordingAudioGateTests
{
    private const int Rate = 48_000;
    private const int Burst = 3072; // 64 ms
    private static readonly long Origin = Stopwatch.Frequency * 1000;

    private static long At(double ms) => Origin + (long)Math.Round(ms * Stopwatch.Frequency / 1000);

    private static int Frames(double ms) => (int)Math.Round(ms * Rate / 1000);

    [Fact]
    public void ABurstThatBeganBeforeTheFirstFrame_KeepsOnlyWhatCameAfterIt()
    {
        // 64 ms that arrived 30 ms after the first frame: the first 34 ms of it came before.
        var kept = RecordingAudioGate.FramesInside(At(130), Burst, Rate, opened: At(100), closed: RecordingAudioGate.Never);

        Assert.Equal((Frames(34), Burst), kept);
    }

    [Fact]
    public void ABurstThatRanPastTheStop_KeepsOnlyWhatCameBeforeIt()
    {
        var kept = RecordingAudioGate.FramesInside(At(1040), Burst, Rate, opened: At(100), closed: At(1000));

        Assert.Equal((0, Frames(24)), kept);
    }

    [Fact]
    public void ABurstBetweenTheFirstFrameAndTheStop_IsKeptWhole()
    {
        var kept = RecordingAudioGate.FramesInside(At(500), Burst, Rate, opened: At(100), closed: At(1000));

        Assert.Equal((0, Burst), kept);
    }

    [Fact]
    public void ABurstAroundAWholeRecording_KeepsTheStretchBetweenItsEdges()
    {
        var kept = RecordingAudioGate.FramesInside(At(64), Burst, Rate, opened: At(20), closed: At(50));

        Assert.Equal((Frames(20), Frames(50)), kept);
    }

    [Fact]
    public void BurstsOutsideTheGate_AreDropped()
    {
        long never = RecordingAudioGate.Never;
        Assert.Equal((0, 0), RecordingAudioGate.FramesInside(At(500), Burst, Rate, opened: never, closed: never));
        Assert.Equal((0, 0), RecordingAudioGate.FramesInside(At(90), Burst, Rate, opened: At(100), closed: never));
        Assert.Equal((0, 0), RecordingAudioGate.FramesInside(At(1100), Burst, Rate, opened: At(100), closed: At(1000)));
    }

    [Fact]
    public void TheGate_IsOpenFromTheFirstFrameToTheStop_ForEachSegment()
    {
        var gate = new RecordingAudioGate();
        Assert.False(gate.IsOpen);

        gate.Open(At(100));
        Assert.True(gate.IsOpen);
        gate.Close(At(1000));
        Assert.False(gate.IsOpen);

        // A resume starts the next segment shut again, until its own first frame.
        gate.Reset();
        Assert.False(gate.IsOpen);
        gate.Open(At(5000));
        Assert.True(gate.IsOpen);
    }

    [Fact]
    public void AFeed_ReachesTheStop_OnTheFirstBurstToArriveAfterIt()
    {
        var gate = new RecordingAudioGate();
        gate.Open(At(100));
        var feed = gate.NewFeed();
        feed.Admit(At(500), Burst, Rate);
        gate.Close(At(1000));

        feed.Admit(At(990), Burst, Rate);
        Assert.False(feed.ReachedClose.IsCompleted, "a burst that arrived before the stop leaves its audio after it to come");

        // Empty: there was nothing more to hand over.
        feed.Admit(At(1010), 0, Rate);
        Assert.True(feed.ReachedClose.IsCompletedSuccessfully);
    }

    [Fact]
    public void AFeed_DescribesWhatItCutAtEachEdge_AndHowItsAudioEnded()
    {
        var gate = new RecordingAudioGate();
        gate.Open(At(100));
        var feed = gate.NewFeed();
        Assert.Equal("head=none tail=none pad=none", feed.Describe());

        feed.Admit(At(90), Burst, Rate); // all of it before the first frame
        feed.Admit(At(130), Burst, Rate);
        feed.Admit(At(500), Burst, Rate);
        gate.Close(At(1000));
        feed.Admit(At(1040), Burst, Rate);
        feed.NotePad(Frames(72), Rate);

        Assert.Equal("head=34/64ms tail=24/64ms pad=72ms", feed.Describe());
    }

    [Fact]
    public void TheWrittenAudio_StartsAtTheFirstFrame_As16BitPcm()
    {
        var gate = new RecordingAudioGate();
        gate.Open(At(100));
        var source = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);
        var target = new WaveFormat(Rate, 16, 2);

        // Each frame holds its own number, so the first one written says where the cut fell.
        var burst = new byte[Burst * source.BlockAlign];
        for (int i = 0; i < Burst; i++)
        {
            BitConverter.TryWriteBytes(burst.AsSpan(i * 8), i / 4096f);
            BitConverter.TryWriteBytes(burst.AsSpan(i * 8 + 4), i / 4096f);
        }

        var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(wav), target))
        {
            RecordingService.WriteInsideGate(gate.NewFeed(), At(130), writer, muted: false, burst, burst.Length, source, target);
        }

        wav.Position = 0;
        using var reader = new WaveFileReader(wav);
        Assert.Equal(Burst - Frames(34), reader.Length / target.BlockAlign);
        var first = new byte[target.BlockAlign];
        Assert.Equal(first.Length, reader.Read(first, 0, first.Length));
        Assert.Equal((short)Math.Round(Frames(34) / 4096f * short.MaxValue), BitConverter.ToInt16(first, 0));
    }

    [Fact]
    public void ASegmentsAudio_EndsInSilence_WhereItsVideoEnds()
    {
        var format = new WaveFormat(Rate, 16, 1);
        var feed = new RecordingAudioGate().NewFeed();
        var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(wav), format))
        {
            var audio = new byte[Frames(100) * format.BlockAlign];
            Array.Fill(audio, (byte)0x11);
            writer.Write(audio, 0, audio.Length);

            RecordingService.PadToVideoLength(writer, TimeSpan.FromMilliseconds(172), feed);
        }

        wav.Position = 0;
        using var reader = new WaveFileReader(wav);
        var written = new byte[reader.Length];
        Assert.Equal(written.Length, reader.Read(written, 0, written.Length));
        Assert.Equal(Frames(172) * format.BlockAlign, written.Length);
        Assert.All(written[(Frames(100) * format.BlockAlign)..], b => Assert.Equal(0, b));
        Assert.Equal("head=none tail=none pad=72ms", feed.Describe());
    }

    [Fact]
    public void AudioThatRanPastItsVideo_IsLeftAsItIs()
    {
        var format = new WaveFormat(Rate, 16, 1);
        var feed = new RecordingAudioGate().NewFeed();
        using var writer = new WaveFileWriter(new MemoryStream(), format);
        writer.Write(new byte[Frames(100) * format.BlockAlign], 0, Frames(100) * format.BlockAlign);

        RecordingService.PadToVideoLength(writer, TimeSpan.FromMilliseconds(95), feed);

        Assert.Equal(Frames(100) * format.BlockAlign, writer.Length);
        Assert.Equal("head=none tail=none pad=-5ms", feed.Describe());
    }
}
