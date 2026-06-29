using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Main;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// Real-encode integration check for the Compress pipeline (exercises LibavClipExporter end to end with
/// the new options). It is GATED: a no-op pass unless COMPRESS_IT_SOURCE (an existing video) and
/// COMPRESS_IT_OUTDIR are set, so it never runs on CI / Linux. Requires Windows + the bundled FFmpeg libs.
/// Drive it via <c>scripts/test-compress.ps1</c>, or set the two env vars and run:
///   dotnet test --filter FullyQualifiedName~CompressIntegration
/// </summary>
public class CompressIntegrationTests
{
    private static string? Source => Environment.GetEnvironmentVariable("COMPRESS_IT_SOURCE");
    private static string? OutDir => Environment.GetEnvironmentVariable("COMPRESS_IT_OUTDIR");

    private static bool Enabled =>
        !string.IsNullOrWhiteSpace(Source) && File.Exists(Source) && !string.IsNullOrWhiteSpace(OutDir);

    [Fact]
    public async Task EncodeMatrix_ProducesCorrectOutputs()
    {
        if (!Enabled)
        {
            return; // gate: does nothing unless the runner provides a source + output dir
        }

        string source = Source!;
        string outDir = OutDir!;
        Directory.CreateDirectory(outDir);

        var player = new LibavVideoFramePlayer();
        double duration = await player.ProbeDurationSecondsAsync(source) ?? 0;
        Assert.True(duration > 0, "source duration unreadable");
        var srcSize = await player.ProbeVideoSizeAsync(source);
        Assert.NotNull(srcSize);

        var ranges = new[] { new LibavClipExporter.SourceRange(0, duration) };

        async Task<string> Run(string name, LibavExportOptions opt, string ext = ".mp4")
        {
            string outPath = Path.Combine(outDir, name + ext);
            bool ok = await Task.Run(() =>
                LibavClipExporter.TryExport(source, ranges, outPath, VideoQuality.Medium, options: opt));
            Assert.True(ok && File.Exists(outPath) && new FileInfo(outPath).Length > 0, $"{name}: no output produced");
            return outPath;
        }

        async Task AssertHeight(string path, int expected)
        {
            var size = await player.ProbeVideoSizeAsync(path);
            Assert.NotNull(size);
            Assert.Equal(expected, size!.Value.Height);
            Assert.Equal(0, size.Value.Width % 2);
        }

        // Baseline keeps the source resolution and audio.
        string baseline = await Run("baseline", new LibavExportOptions());
        await AssertHeight(baseline, srcSize!.Value.Height);
        Assert.True(await player.ProbeHasAudioAsync(baseline), "baseline lost audio");

        // Downscale.
        await AssertHeight(await Run("scale720", new LibavExportOptions { MaxHeight = 720 }), 720);
        await AssertHeight(await Run("scale1080", new LibavExportOptions { MaxHeight = 1080 }), 1080);

        // CRF: lower CRF must produce a bigger file than higher CRF.
        long crf18 = new FileInfo(await Run("crf18", new LibavExportOptions { CrfOverride = 18 })).Length;
        long crf30 = new FileInfo(await Run("crf30", new LibavExportOptions { CrfOverride = 30 })).Length;
        Assert.True(crf30 < crf18, $"crf30 ({crf30}) should be smaller than crf18 ({crf18})");

        // Preset just needs to succeed.
        await Run("preset_slow", new LibavExportOptions { Preset = "slow" });

        // H.265 + downscale.
        await AssertHeight(await Run("h265_720", new LibavExportOptions { Codec = VideoCodec.H265, MaxHeight = 720 }), 720);

        // Drop audio -> no audio track.
        string noAudio = await Run("noaudio", new LibavExportOptions { DropAudio = true });
        Assert.False(await player.ProbeHasAudioAsync(noAudio), "drop-audio output still has audio");

        // MKV must carry an audio track (exercises AAC-into-Matroska, the recording-bug-adjacent path).
        string mkv = await Run("with_audio", new LibavExportOptions(), ".mkv");
        Assert.True(await player.ProbeHasAudioAsync(mkv), "mkv output has no audio");

        // Target ~5 MB: output should land at or under target (+20% tolerance for ABR + container overhead).
        int kbps = MainWindowViewModel.ComputeTargetVideoBitrateKbps(5, duration);
        long target = new FileInfo(await Run("target5mb", new LibavExportOptions { TargetVideoBitrateKbps = kbps })).Length;
        Assert.True(target <= 5L * 1024 * 1024 * 12 / 10, $"target5mb={target} exceeds 5 MB + 20%");
    }
}
