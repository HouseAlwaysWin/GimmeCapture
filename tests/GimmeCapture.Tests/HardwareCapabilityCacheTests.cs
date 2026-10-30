using System;
using System.Collections.Generic;
using System.IO;
using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

public sealed class HardwareCapabilityCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "GimmeCapture-HwCaps-" + Guid.NewGuid().ToString("N"));
    private readonly List<bool> _recalled = new();
    private int _probes;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string CachePath => Path.Combine(_directory, HardwareCapabilityCache.FileName);

    private HardwareCapabilityCache Cache(string? key, HardwareCapabilityCache.Capabilities? probeAnswer) =>
        new(
            () => _directory,
            () => key,
            () =>
            {
                _probes++;
                return probeAnswer;
            },
            _recalled.Add);

    [Fact]
    public void FirstRunProbesAndKeepsTheAnswer()
    {
        var answer = Cache("k1", new(HardwareAv1: true, Gif: true)).Get();

        Assert.Equal(new HardwareCapabilityCache.Capabilities(true, true), answer);
        Assert.Equal(1, _probes);
        Assert.Empty(_recalled);
        Assert.True(File.Exists(CachePath));
    }

    [Fact]
    public void LaterRunOnTheSameMachineTakesTheKeptAnswerWithoutProbing()
    {
        Cache("k1", new(HardwareAv1: false, Gif: true)).Get();
        _probes = 0;

        var answer = Cache("k1", new(HardwareAv1: true, Gif: false)).Get();

        Assert.Equal(new HardwareCapabilityCache.Capabilities(false, true), answer);
        Assert.Equal(0, _probes);
        Assert.Equal(new[] { false }, _recalled);
    }

    [Fact]
    public void ChangedMachineKeyProbesAgainAndKeepsTheNewAnswer()
    {
        Cache("k1", new(HardwareAv1: false, Gif: true)).Get();
        _probes = 0;

        var answer = Cache("k2", new(HardwareAv1: true, Gif: true)).Get();

        Assert.Equal(new HardwareCapabilityCache.Capabilities(true, true), answer);
        Assert.Equal(1, _probes);
        Assert.Empty(_recalled);

        _probes = 0;
        Cache("k2", new(HardwareAv1: false, Gif: false)).Get();
        Assert.Equal(0, _probes);
        Assert.Equal(new[] { true }, _recalled);
    }

    [Fact]
    public void WithoutAMachineKeyEveryRunProbesAndNothingIsKept()
    {
        var answer = Cache(null, new(HardwareAv1: true, Gif: true)).Get();

        Assert.Equal(new HardwareCapabilityCache.Capabilities(true, true), answer);
        Assert.Equal(1, _probes);
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public void AProbeThatReachedNoAnswerIsNotKept()
    {
        var answer = Cache("k1", null).Get();

        Assert.Null(answer);
        Assert.False(File.Exists(CachePath));

        Cache("k1", new(HardwareAv1: true, Gif: true)).Get();
        Assert.Equal(2, _probes);
    }

    [Fact]
    public void AnUnreadableFileCountsAsNoAnswerAndIsReplaced()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(CachePath, "{ not json");

        var answer = Cache("k1", new(HardwareAv1: false, Gif: true)).Get();

        Assert.Equal(new HardwareCapabilityCache.Capabilities(false, true), answer);
        Assert.Equal(1, _probes);
        _probes = 0;
        Cache("k1", new(HardwareAv1: true, Gif: true)).Get();
        Assert.Equal(0, _probes);
    }

    [Fact]
    public void AMachineKeyThatThrowsProbesAsBeforeAndKeepsNothing()
    {
        var cache = new HardwareCapabilityCache(
            () => _directory,
            () => throw new InvalidOperationException("registry gone"),
            () =>
            {
                _probes++;
                return new HardwareCapabilityCache.Capabilities(true, true);
            },
            _recalled.Add);

        Assert.Equal(new HardwareCapabilityCache.Capabilities(true, true), cache.Get());
        Assert.Equal(1, _probes);
        Assert.False(File.Exists(CachePath));
    }

    [WindowsFact]
    public void TheMachineKeyOnWindowsNamesTheProbeVersionAndIsStable()
    {
        string? first = HardwareCapabilityCache.ReadMachineKey();
        string? second = HardwareCapabilityCache.ReadMachineKey();

        Assert.NotNull(first);
        Assert.StartsWith("probe=", first);
        Assert.Equal(first, second);
    }
}
