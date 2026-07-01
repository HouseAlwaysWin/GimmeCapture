using System.Collections.Generic;
using System.Text.Json;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

// Serialization shape for the two new Compress persistence stores: per-file edit/done state and the
// last-used session settings. Mirrors CompressPresetTests (model-level round-trip, no file I/O).
public class CompressPersistenceTests
{
    [Fact]
    public void ItemState_RoundTrips_ThroughJson()
    {
        var original = new CompressItemState { Rotation = 270, OutputName = @"sub\clip", Done = true };

        string json = JsonSerializer.Serialize(original);
        CompressItemState? round = JsonSerializer.Deserialize<CompressItemState>(json);

        Assert.NotNull(round);
        Assert.Equal(270, round!.Rotation);
        Assert.Equal(@"sub\clip", round.OutputName);
        Assert.True(round.Done);
    }

    [Fact]
    public void ItemState_RoundTrips_TrimFields()
    {
        var original = new CompressItemState
        {
            Rotation = 90,
            OutputName = "clip",
            TrimEnabled = true,
            TrimStart = 5m,
            TrimEnd = 42m
        };

        string json = JsonSerializer.Serialize(original);
        CompressItemState? round = JsonSerializer.Deserialize<CompressItemState>(json);

        Assert.NotNull(round);
        Assert.True(round!.TrimEnabled);
        Assert.Equal(5m, round.TrimStart);
        Assert.Equal(42m, round.TrimEnd);
    }

    [Fact]
    public void ItemState_RoundTrips_Segments()
    {
        var original = new CompressItemState
        {
            Rotation = 90,
            OutputName = "clip",
            TrimEnabled = true,
            Segments = new List<TrimSegment>
            {
                new() { Start = 5m, End = 15m },
                new() { Start = 30m, End = 45m }
            }
        };

        string json = JsonSerializer.Serialize(original);
        CompressItemState? round = JsonSerializer.Deserialize<CompressItemState>(json);

        Assert.NotNull(round);
        Assert.True(round!.TrimEnabled);
        Assert.NotNull(round.Segments);
        Assert.Equal(2, round.Segments!.Count);
        Assert.Equal(5m, round.Segments[0].Start);
        Assert.Equal(45m, round.Segments[1].End);
    }

    [Fact]
    public void ItemStateMap_RoundTrips_KeyedByPath()
    {
        var map = new Dictionary<string, CompressItemState>
        {
            [@"D:\Videos\a.mp4"] = new CompressItemState { Rotation = 90, OutputName = "a", Done = false },
            [@"D:\Videos\b.mp4"] = new CompressItemState { Rotation = 0, OutputName = "", Done = true }
        };

        string json = JsonSerializer.Serialize(map);
        var round = JsonSerializer.Deserialize<Dictionary<string, CompressItemState>>(json);

        Assert.NotNull(round);
        Assert.Equal(2, round!.Count);
        Assert.Equal(90, round[@"D:\Videos\a.mp4"].Rotation);
        Assert.True(round[@"D:\Videos\b.mp4"].Done);
    }

    [Fact]
    public void SessionState_RoundTrips_SettingsAndBatchOptions()
    {
        var original = new CompressSessionState
        {
            Settings = new CompressPreset
            {
                Codec = VideoCodec.H265,
                MaxHeight = 1080,
                Crf = 20,
                UseTargetSize = true,
                TargetSizeMB = 40m,
                AudioChannels = 1
            },
            OutputFolder = @"D:\Out",
            AppendDate = false,
            ParallelCount = 3
        };

        string json = JsonSerializer.Serialize(original);
        CompressSessionState? round = JsonSerializer.Deserialize<CompressSessionState>(json);

        Assert.NotNull(round);
        Assert.Equal(VideoCodec.H265, round!.Settings.Codec);
        Assert.Equal(1080, round.Settings.MaxHeight);
        Assert.Equal(20, round.Settings.Crf);
        Assert.True(round.Settings.UseTargetSize);
        Assert.Equal(40m, round.Settings.TargetSizeMB);
        Assert.Equal(1, round.Settings.AudioChannels);
        Assert.Equal(@"D:\Out", round.OutputFolder);
        Assert.False(round.AppendDate);
        Assert.Equal(3, round.ParallelCount);
    }

    [Fact]
    public void SegmentState_RoundTrips_IncludingCompletedChunks()
    {
        var original = new CompressSegmentState
        {
            InputPath = @"D:\Videos\long.mp4",
            OutputPath = @"D:\Videos\long_small.mp4",
            SettingsKey = "H264|23|veryfast|0|0|False|128|2|90",
            TotalDuration = 305.0,
            ChunkSeconds = 30.0,
            ChunkCount = 11,
            CompletedChunks = new List<int> { 0, 1, 2, 3 }
        };

        string json = JsonSerializer.Serialize(original);
        CompressSegmentState? round = JsonSerializer.Deserialize<CompressSegmentState>(json);

        Assert.NotNull(round);
        Assert.Equal(original.SettingsKey, round!.SettingsKey);
        Assert.Equal(305.0, round.TotalDuration);
        Assert.Equal(11, round.ChunkCount);
        Assert.Equal(new List<int> { 0, 1, 2, 3 }, round.CompletedChunks);
    }
}
