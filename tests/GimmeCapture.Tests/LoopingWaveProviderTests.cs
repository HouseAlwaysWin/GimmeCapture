using System;
using System.Collections.Generic;
using System.IO;
using GimmeCapture.Services.Core.Media;
using NAudio.Wave;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// A video pin's looping sound (<see cref="LoopingWaveProvider"/>): one stream read over and over, never reopened.
/// The source is 16-bit mono at 1 kHz, each sample holding 100 + its index, so the samples read say exactly which part
/// of the stream played; silence reads as 0.
/// </summary>
public class LoopingWaveProviderTests
{
    private static readonly WaveFormat Format = new(1000, 16, 1);

    [Fact]
    public void TheFirstPassRunsFromTheStart_ThenTheLoopPlaysOverAndOver()
    {
        var provider = new LoopingWaveProvider(Source(10), 0.005, new PlaybackLoop(0.002, 0.008));

        Assert.Equal(
            new[] { 105, 106, 107, 102, 103, 104, 105, 106, 107, 102, 103, 104, 105, 106, 107, 102 },
            ReadSamples(provider, 16, chunk: 16));
    }

    [Fact]
    public void AReadThatCrossesTheLoopsEnd_GoesOnFromItsStart_InTheSameRead()
    {
        // The device asks for whatever its buffer has room for, which never lines up with the loop.
        var expected = new List<int>();
        for (int i = 0; i < 40; i++)
        {
            expected.Add(102 + (i % 6));
        }

        var provider = new LoopingWaveProvider(Source(10), 0.002, new PlaybackLoop(0.002, 0.008));

        Assert.Equal(expected, ReadSamples(provider, 40, chunk: 7));
    }

    [Fact]
    public void ASoundThatEndsBeforeTheLoop_IsFilledWithSilenceToTheLoopsEnd_EveryPass()
    {
        // The loop runs to 12 ms, the sound to 10 ms: each pass lasts the loop's 6 ms all the same.
        var provider = new LoopingWaveProvider(Source(10), 0.006, new PlaybackLoop(0.006, 0.012));

        Assert.Equal(
            new[] { 106, 107, 108, 109, 0, 0, 106, 107, 108, 109, 0, 0, 106, 107 },
            ReadSamples(provider, 14, chunk: 5));
    }

    [Fact]
    public void ASoundThatRunsOnPastTheLoop_IsCutAtItsEnd()
    {
        var provider = new LoopingWaveProvider(Source(100), 0, new PlaybackLoop(0, 0.003));

        Assert.Equal(new[] { 100, 101, 102, 100, 101, 102, 100 }, ReadSamples(provider, 7, chunk: 7));
    }

    [Theory]
    [InlineData(0.009)] // past the loop's end
    [InlineData(0.001)] // before its start
    public void APlaybackAskedToBeginOutsideTheLoop_BeginsAtItsStart(double startSeconds)
    {
        var provider = new LoopingWaveProvider(Source(10), startSeconds, new PlaybackLoop(0.002, 0.008));

        Assert.Equal(new[] { 102, 103, 104 }, ReadSamples(provider, 3, chunk: 3));
    }

    [Fact]
    public void BytesAt_LandsOnAWholeSampleFrame()
    {
        var stereo = new WaveFormat(44100, 16, 2);

        long bytes = LoopingWaveProvider.BytesAt(1.00001, stereo);

        Assert.Equal(0, bytes % stereo.BlockAlign);
        Assert.Equal(44100 * 4, bytes);
        Assert.Equal(0, LoopingWaveProvider.BytesAt(-1, stereo));
    }

    private static RawSourceWaveStream Source(int samples)
    {
        var bytes = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (short)(100 + i));
        }

        return new RawSourceWaveStream(new MemoryStream(bytes), Format);
    }

    private static List<int> ReadSamples(IWaveProvider provider, int samples, int chunk)
    {
        var result = new List<int>();
        var buffer = new byte[chunk * 2];
        while (result.Count < samples)
        {
            int wanted = Math.Min(chunk, samples - result.Count) * 2;
            int read = provider.Read(buffer, 0, wanted);
            Assert.Equal(wanted, read);
            for (int i = 0; i < read; i += 2)
            {
                result.Add(BitConverter.ToInt16(buffer, i));
            }
        }

        return result;
    }
}
