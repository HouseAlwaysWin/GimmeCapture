using System.Collections.Generic;
using System.IO;
using System.Linq;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

public sealed class CompressProbeCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", nameof(CompressProbeCacheTests), Guid.NewGuid().ToString("N"));

    private string File_ => Path.Combine(_dir, "cache.json");

    public CompressProbeCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void WhatWasWritten_IsReadBack_WhateverTheCaseOfThePath()
    {
        var written = new Dictionary<string, CompressProbeEntry>
        {
            [@"F:\Clips\A.mp4"] = new()
            {
                Length = 123, LastWriteUtcTicks = 456, ProbedUtcTicks = 789, Duration = 12.5, Width = 3840, Height = 2160, Fps = 29,
                ThumbnailWidth = 96, ThumbnailHeight = 54, ThumbnailJpeg = new byte[] { 1, 2, 3 },
            },
        };

        CompressProbeCacheService.Write(File_, written);
        CompressProbeEntry read = CompressProbeCacheService.Load(File_)[@"f:\clips\a.mp4"];

        Assert.Equal(123, read.Length);
        Assert.Equal(12.5, read.Duration);
        Assert.Equal(3840, read.Width);
        Assert.Equal(2160, read.Height);
        Assert.Equal(29, read.Fps);
        Assert.Equal(96, read.ThumbnailWidth);
        Assert.Equal(new byte[] { 1, 2, 3 }, read.ThumbnailJpeg);
    }

    [Fact]
    public void NoFile_AndABrokenFile_ReadAsNothingRemembered()
    {
        Assert.Empty(CompressProbeCacheService.Load(File_));

        File.WriteAllText(File_, "{ not json");
        Assert.Empty(CompressProbeCacheService.Load(File_));
    }

    [Fact]
    public void AnEntryDescribesOnlyTheFileAsItWas()
    {
        var when = new DateTime(2026, 11, 9, 1, 2, 3, DateTimeKind.Utc);
        var entry = new CompressProbeEntry { Length = 100, LastWriteUtcTicks = when.Ticks };

        Assert.True(entry.Describes(100, when));
        Assert.False(entry.Describes(101, when));
        Assert.False(entry.Describes(100, when.AddSeconds(1)));
    }

    [Fact]
    public void OfTooManyEntries_TheOldestGo()
    {
        var entries = Enumerable.Range(0, 5).ToDictionary(i => $"file{i}", i => new CompressProbeEntry { ProbedUtcTicks = i });

        IReadOnlyDictionary<string, CompressProbeEntry> kept = CompressProbeCacheService.Newest(entries, 2);

        Assert.Equal(new[] { "file3", "file4" }, kept.Keys.OrderBy(k => k));
        Assert.Same(entries, CompressProbeCacheService.Newest(entries, 5));
    }

    [Fact]
    public void APicture_ComesBackAtItsSize_AndNearItsColours()
    {
        const int Width = 96, Height = 54;
        var bgra = new byte[Width * Height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 40;      // blue
            bgra[i + 1] = 90;  // green
            bgra[i + 2] = 200; // red
            bgra[i + 3] = 255;
        }

        byte[]? jpeg = CompressThumbnailCodec.Encode(new LibavVideoFramePlayer.BgraThumbnail(Width, Height, bgra));
        Assert.NotNull(jpeg);
        Assert.InRange(jpeg!.Length, 1, Width * Height * 4 / 4);

        LibavVideoFramePlayer.BgraThumbnail? back = CompressThumbnailCodec.Decode(
            new CompressProbeEntry { ThumbnailWidth = Width, ThumbnailHeight = Height, ThumbnailJpeg = jpeg });

        Assert.NotNull(back);
        Assert.Equal((Width, Height), (back!.Width, back.Height));
        Assert.Equal(bgra.Length, back.Bgra.Length);
        Assert.InRange(back.Bgra[0], 40 - 8, 40 + 8);
        Assert.InRange(back.Bgra[1], 90 - 8, 90 + 8);
        Assert.InRange(back.Bgra[2], 200 - 8, 200 + 8);
    }

    [Fact]
    public void AnEntryWithNoUsablePicture_GivesNone()
    {
        Assert.Null(CompressThumbnailCodec.Decode(new CompressProbeEntry { ThumbnailWidth = 96, ThumbnailHeight = 54 }));
        Assert.Null(CompressThumbnailCodec.Decode(new CompressProbeEntry { ThumbnailWidth = 96, ThumbnailHeight = 54, ThumbnailJpeg = new byte[] { 1, 2, 3 } }));
        Assert.Null(CompressThumbnailCodec.Decode(new CompressProbeEntry { ThumbnailJpeg = new byte[] { 1, 2, 3 } }));
    }
}
