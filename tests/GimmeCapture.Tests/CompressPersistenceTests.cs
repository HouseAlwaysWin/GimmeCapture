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

    [Fact]
    public void EditStateMapper_RoundTrips_Annotations()
    {
        var pen = new Annotation
        {
            Type = AnnotationType.Pen,
            Color = Avalonia.Media.Colors.Lime,
            Thickness = 4,
        };
        pen.AddPoint(new Avalonia.Point(1, 2));
        pen.AddPoint(new Avalonia.Point(3, 4));
        var text = new Annotation
        {
            Type = AnnotationType.Text,
            StartPoint = new Avalonia.Point(10, 20),
            EndPoint = new Avalonia.Point(10, 20),
            Text = "hello 世界",
            FontSize = 24,
            FontFamily = new Avalonia.Media.FontFamily("Consolas"),
            IsBold = true,
        };
        var blur = new Annotation
        {
            Type = AnnotationType.Blur,
            StartPoint = new Avalonia.Point(5, 5),
            EndPoint = new Avalonia.Point(50, 40),
            EffectSettings = new AnnotationEffectSettings { BlurRadius = 28f, MosaicCellSize = 20 },
        };

        string json = JsonSerializer.Serialize(CompressEditStateMapper.ToState(new[] { pen, text, blur }));
        var round = CompressEditStateMapper.FromState(
            JsonSerializer.Deserialize<List<CompressAnnotationState>>(json)!);

        Assert.Equal(3, round.Count);
        Assert.Equal(AnnotationType.Pen, round[0].Type);
        Assert.Equal(2, round[0].Points.Count);
        Assert.Equal(3, round[0].Points[1].X, 3);
        Assert.Equal(Avalonia.Media.Colors.Lime, round[0].Color);
        Assert.Equal("hello 世界", round[1].Text);
        Assert.Equal("Consolas", round[1].FontFamily.Name);
        Assert.True(round[1].IsBold);
        Assert.Equal(28f, round[2].EffectSettings.BlurRadius);
        Assert.Equal(20, round[2].EffectSettings.MosaicCellSize);
    }

    [Fact]
    public void EditStateMapper_RoundTrips_RedactionTracks()
    {
        var track = new RedactionTrack { Effect = RedactionEffect.Mosaic };
        track.Keyframes.Add(new RedactionKeyframe { TimeSeconds = 1.5, X = 0.1, Y = 0.2, Width = 0.3, Height = 0.4 });
        track.Keyframes.Add(new RedactionKeyframe { TimeSeconds = 4.0, X = 0.5, Y = 0.5, Width = 0.2, Height = 0.2 });

        string json = JsonSerializer.Serialize(CompressEditStateMapper.ToState(new[] { track }));
        var round = CompressEditStateMapper.FromState(
            JsonSerializer.Deserialize<List<CompressRedactionTrackState>>(json)!);

        Assert.Single(round);
        Assert.Equal(RedactionEffect.Mosaic, round[0].Effect);
        Assert.Equal(2, round[0].Keyframes.Count);
        Assert.Equal(1.5, round[0].Keyframes[0].TimeSeconds, 3);
        Assert.Equal(0.4, round[0].Keyframes[0].Height, 3);
    }

    [Fact]
    public void EditStateMapper_UnknownTypes_AreSkippedNotFatal()
    {
        var states = new List<CompressAnnotationState>
        {
            new() { Type = "SomeFutureTool" },
            new() { Type = nameof(AnnotationType.Rectangle), ColorHex = "not-a-color" },
        };

        var round = CompressEditStateMapper.FromState(states);

        Assert.Single(round); // unknown type dropped; bad color falls back instead of throwing
        Assert.Equal(Avalonia.Media.Colors.Red, round[0].Color);
    }

    [Fact]
    public void ItemState_RoundTrips_SegmentSpeed_AndCrop()
    {
        var original = new CompressItemState
        {
            TrimEnabled = true,
            Segments = new List<TrimSegment> { new() { Start = 0m, End = 10m, Speed = 2m } },
            CropX = 4,
            CropY = 8,
            CropWidth = 640,
            CropHeight = 480
        };

        string json = JsonSerializer.Serialize(original);
        CompressItemState? round = JsonSerializer.Deserialize<CompressItemState>(json);

        Assert.NotNull(round);
        Assert.Equal(2m, round!.Segments![0].Speed);
        Assert.Equal(4, round.CropX);
        Assert.Equal(640, round.CropWidth);
        Assert.Equal(480, round.CropHeight);
    }
}
