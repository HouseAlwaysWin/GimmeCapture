using System;
using System.Collections.Generic;
using System.IO;
using GimmeCapture.Services.Core.Media;
using NAudio.Wave;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// A video pin's sound through its cuts (<see cref="PiecesWaveProvider"/>): one stream read straight through what was cut
/// out, never reopened. The source is 16-bit mono at 1 kHz, each sample holding 100 + its index, so the samples read say
/// exactly which part of the stream played; silence reads as 0.
/// </summary>
public class PiecesWaveProviderTests
{
    private static readonly WaveFormat Format = new(1000, 16, 1);

    private static SoundPieces Pieces(double start, bool goesRound, int first, params (double Start, double End)[] pieces)
    {
        var list = new List<PlaybackPiece>();
        foreach ((double s, double e) in pieces)
        {
            list.Add(new PlaybackPiece(s, e, 1.0));
        }

        return new SoundPieces(list, first, start, goesRound);
    }

    [Fact]
    public void ThePieces_PlayOneAfterTheOther_WhatLiesBetweenLeftOut()
    {
        var provider = new PiecesWaveProvider(Source(20), Pieces(0.002, false, 0, (0.002, 0.004), (0.010, 0.013)));

        Assert.Equal(new[] { 102, 103, 110, 111, 112 }, ReadSamples(provider, 5, chunk: 5));
    }

    [Fact]
    public void AReadThatCrossesACut_GoesOnInTheSameRead()
    {
        // The device asks for whatever its buffer has room for, which never lines up with the cuts.
        var provider = new PiecesWaveProvider(Source(20), Pieces(0.002, false, 0, (0.002, 0.004), (0.010, 0.013)));

        Assert.Equal(new[] { 102, 103, 110, 111, 112 }, ReadSamples(provider, 5, chunk: 3));
    }

    [Fact]
    public void ThePlayback_BeginsPartwayThroughALaterPiece()
    {
        var provider = new PiecesWaveProvider(Source(20), Pieces(0.011, false, 1, (0.002, 0.004), (0.010, 0.013)));

        Assert.Equal(new[] { 111, 112 }, ReadSamples(provider, 2, chunk: 2));
    }

    [Fact]
    public void ARound_BeginsAgainAtTheFirstPiecesStart()
    {
        var provider = new PiecesWaveProvider(Source(20), Pieces(0.012, true, 1, (0.002, 0.004), (0.010, 0.013)));

        Assert.Equal(new[] { 112, 102, 103, 110, 111, 112, 102 }, ReadSamples(provider, 7, chunk: 4));
    }

    [Fact]
    public void AfterTheLastPiece_ComesSilence_WithTheRequestStillFilled()
    {
        // The device goes on until the playback stops it, so its clock runs on under the last frame.
        var provider = new PiecesWaveProvider(Source(20), Pieces(0.002, false, 0, (0.002, 0.004)));

        Assert.Equal(new[] { 102, 103, 0, 0, 0 }, ReadSamples(provider, 5, chunk: 5));
    }

    [Fact]
    public void ASoundThatEndsBeforeAPiece_IsFilledWithSilenceToItsEnd()
    {
        var provider = new PiecesWaveProvider(Source(10), Pieces(0.008, true, 0, (0.008, 0.012), (0.002, 0.003)));

        Assert.Equal(new[] { 108, 109, 0, 0, 102, 108 }, ReadSamples(provider, 6, chunk: 4));
    }

    [Fact]
    public void AnEmptyPiece_CannotKeepAReadFromEnding()
    {
        var provider = new PiecesWaveProvider(Source(20), Pieces(0.002, true, 0, (0.002, 0.002)));

        Assert.Equal(new[] { 102, 102, 102 }, ReadSamples(provider, 3, chunk: 3));
    }

    [Fact]
    public void Stretches_ArePlayedInOrder_FromWhereThePlaybackBegins()
    {
        SoundPieces sound = Pieces(9.5, false, 1, (1, 3), (8, 11), (17, 20));

        Assert.Equal(new[] { (9.5, 1.5), (17.0, 3.0) }, sound.Stretches());
    }

    [Fact]
    public void Stretches_GoRound_WhenTheSoundDoes()
    {
        SoundPieces sound = Pieces(2, true, 0, (1, 3), (8, 9));

        var stretches = new List<(double, double)>();
        foreach ((double, double) stretch in sound.Stretches())
        {
            stretches.Add(stretch);
            if (stretches.Count == 5)
            {
                break;
            }
        }

        Assert.Equal(new[] { (2.0, 1.0), (8.0, 1.0), (1.0, 2.0), (8.0, 1.0), (1.0, 2.0) }, stretches);
    }

    [Fact]
    public void Stretches_End_WhenAWholeRoundHasNothingToPlay()
    {
        SoundPieces sound = Pieces(3, true, 0, (3, 3));

        Assert.Empty(sound.Stretches());
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
