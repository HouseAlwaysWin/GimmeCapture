using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// An export's rotation without annotations is a step of its filter graph (<see cref="LibavClipExporter.RotationFilterNodes"/>),
/// where it went through BGRA and SkiaSharp; the picture must turn the same way. Real libav on a small textured clip
/// (<see cref="VideoTestClips"/>), so it runs everywhere.
/// </summary>
public sealed class LibavClipExporterRotationTests(ITestOutputHelper output) : IDisposable
{
    private const int Width = 96;
    private const int Height = 64;

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task ARotatedExport_TurnsThePictureClockwise(int rotation)
    {
        string source = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "source.mkv"), Width, Height, 30, 30, detailed: true);
        string rotated = Path.Combine(_dir, $"rotated{rotation}.mp4");

        bool exported = LibavClipExporter.TryExport(
            source, [new LibavClipExporter.SourceRange(0, 1)], rotated, VideoQuality.High,
            options: new LibavExportOptions { RotationDegrees = rotation, DropAudio = true });

        bool quarter = rotation != 180;
        int outW = quarter ? Height : Width;
        int outH = quarter ? Width : Height;
        byte[] before = (await LibavVideoFramePlayer.DecodeFrameAtAsync(source, 0, Width, Height))!;
        byte[] after = (await LibavVideoFramePlayer.DecodeFrameAtAsync(rotated, 0, outW, outH))!;
        double asked = MeanDifference(Rotate(before, rotation), after);
        double opposite = MeanDifference(Rotate(before, (rotation + 180) % 360), after);
        output.WriteLine($"{rotation}: {asked:F2} levels off the picture turned {rotation}°, {opposite:F2} off it turned the other way");

        Assert.True(exported);
        Assert.True(asked < 6, $"{asked:F2} levels on average off the picture turned {rotation}°.");
        Assert.True(opposite > 4 * asked, $"{opposite:F2} levels off the opposite turn, against {asked:F2}.");
    }

    [Fact]
    public void TheRotations_AreTheGraphsTransposeAndFlips()
    {
        Assert.Empty(LibavClipExporter.RotationFilterNodes(0));
        Assert.Equal([("transpose", "dir=clock")], LibavClipExporter.RotationFilterNodes(90));
        Assert.Equal([("hflip", ""), ("vflip", "")], LibavClipExporter.RotationFilterNodes(180));
        Assert.Equal([("transpose", "dir=cclock")], LibavClipExporter.RotationFilterNodes(270));
        Assert.Equal([("transpose", "dir=cclock")], LibavClipExporter.RotationFilterNodes(-90));
    }

    /// <summary>
    /// How long ten seconds of a real video take to export at 1080 tall, unrotated and rotated, at the default preset
    /// and at slow. Opt-in: ROTATE_PROBE_FILE names the video, which should be 30 s or more.
    /// </summary>
    [OptInFact("ROTATE_PROBE_FILE")]
    public void ARotatedExportOfARealVideo_Timed()
    {
        string input = Environment.GetEnvironmentVariable("ROTATE_PROBE_FILE")!;
        foreach (string preset in new[] { "veryfast", "slow" })
        {
            foreach (int rotation in new[] { 0, 90, 0, 90, 270 })
            {
                string exported = Path.Combine(_dir, $"r{rotation}_{preset}_{Guid.NewGuid():N}.mp4");
                var watch = Stopwatch.StartNew();
                bool ok = LibavClipExporter.TryExport(
                    input, [new LibavClipExporter.SourceRange(30, 40)], exported, VideoQuality.Medium,
                    options: new LibavExportOptions { Preset = preset, MaxHeight = 1080, RotationDegrees = rotation, DropAudio = true });
                output.WriteLine(
                    $"{preset} rotation {rotation,3}: {watch.Elapsed.TotalSeconds:F2} s, " +
                    $"{(ok ? new FileInfo(exported).Length / 1048576.0 : 0):F1} MB");
            }
        }
    }

    // The BGRA picture turned clockwise by a multiple of 90°.
    private static byte[] Rotate(byte[] bgra, int rotation)
    {
        bool quarter = rotation is 90 or 270;
        int outW = quarter ? Height : Width;
        int outH = quarter ? Width : Height;
        var rotated = new byte[bgra.Length];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                (int tx, int ty) = rotation switch
                {
                    90 => (Height - 1 - y, x),
                    180 => (Width - 1 - x, Height - 1 - y),
                    270 => (y, Width - 1 - x),
                    _ => (x, y),
                };
                Array.Copy(bgra, ((y * Width) + x) * 4, rotated, ((ty * outW) + tx) * 4, 4);
            }
        }

        return rotated;
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        long sum = 0;
        int count = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                sum += Math.Abs(a[i + channel] - b[i + channel]);
                count++;
            }
        }

        return (double)sum / count;
    }
}
