using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// The files an export no longer writes on its way: a soundless MP4 export encodes straight into its file instead of
/// into its folder and copying it there, as every chunk of a segmented compress did, and the segmented compress joins its
/// chunks with the sound straight into the output, with no merged.mkv in between. A watch on the temporary folder looks
/// for files of those names; each test also makes one where it should, so a watch that sees nothing has not just
/// missed it. Real libav on tiny clips (<see cref="VideoTestClips"/>), on their own (<see cref="ExportTempFolders"/>).
/// </summary>
[Collection(ExportTempFolders.Name)]
public sealed class ExportTempFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public ExportTempFileTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task ASoundlessMp4Export_IsEncodedStraightIntoItsFile()
    {
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, 90, 30);
        string soundless = Path.Combine(_dir, "soundless.mp4");
        string withSound = Path.Combine(_dir, "with-sound.mp4");

        int soundlessSteps = await CountCreatedAsync("video.mp4", () => Export(clip, soundless, dropAudio: true));
        int withSoundSteps = await CountCreatedAsync("video.mp4", () => Export(clip, withSound, dropAudio: false));

        Assert.Equal(0, soundlessSteps);
        Assert.Equal(1, withSoundSteps); // a video to mux with the sound still goes through the folder
        // The same video either way. (The clip with a tone is Matroska with millisecond times, read as 30.3 frames a
        // second, so neither has exactly its 90 frames.)
        Assert.Equal(VideoTestClips.Read(withSound).Frames, VideoTestClips.Read(soundless).Frames);
        using var player = new LibavVideoFramePlayer();
        Assert.False(await player.ProbeHasAudioAsync(soundless));
        Assert.True(await player.ProbeHasAudioAsync(withSound));
    }

    [Fact]
    public void ASoundlessMp4Export_ThatDoesNotFinish_LeavesNoFile()
    {
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mkv"), 64, 48, 300, 30, detailed: true);
        string output = Path.Combine(_dir, "cancelled.mp4");
        using var cancellation = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(() => LibavClipExporter.TryExport(
            clip, [new LibavClipExporter.SourceRange(0, 10)], output, VideoQuality.Medium,
            cancellationToken: cancellation.Token,
            options: new LibavExportOptions { Preset = "ultrafast", DropAudio = true },
            progress: new InlineProgress(_ => cancellation.Cancel())));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ASegmentedCompress_JoinsItsChunksAndSound_WithNoMergedFile()
    {
        // 70 s: three chunks of 30 s.
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, 2100, 30);
        string output = Path.Combine(_dir, "out.mp4");
        string mergedHere = Path.Combine(_dir, "merged.mkv");

        bool ok = false;
        int merged = await CountCreatedAsync("merged.mkv", async () =>
        {
            ok = await CompressPipeline.EncodeOneFileAsync(
                clip, output, Settings(), 0, 70, new InlineProgress(_ => { }), CancellationToken.None, pauseGate: null);
            await File.WriteAllBytesAsync(mergedHere, [0]); // one the watch has to see
        });

        Assert.True(ok);
        Assert.Equal(1, merged);
        // Every chunk is there: a missing one would take 10 to 30 s off. (The clip's Matroska times make each 30 s chunk
        // end a frame or so long, so not exactly 70 s; LibavMuxerJoinTests compares the join itself with the old one.)
        Assert.InRange(VideoTestClips.Read(output).DurationSeconds, 69.9, 70.3);
        using var player = new LibavVideoFramePlayer();
        Assert.True(await player.ProbeHasAudioAsync(output));
    }

    private static Task Export(string clip, string output, bool dropAudio) => Task.Run(() =>
        Assert.True(LibavClipExporter.TryExport(
            clip, [new LibavClipExporter.SourceRange(0, 3)], output, VideoQuality.Medium,
            options: new LibavExportOptions { Preset = "ultrafast", DropAudio = dropAudio })));

    // How many files named so the temporary folder saw made while the work ran, its events let arrive afterwards.
    private static async Task<int> CountCreatedAsync(string name, Func<Task> work)
    {
        int created = 0;
        using var watcher = new FileSystemWatcher(Path.GetTempPath(), name)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Created += (_, _) => Interlocked.Increment(ref created);
        watcher.EnableRaisingEvents = true;

        await work();
        await OwnThread.Delay(TimeSpan.FromMilliseconds(500));
        return Volatile.Read(ref created);
    }

    private static CompressSettingsSnapshot Settings() => new(
        VideoCodec.H264, 28, MaxHeight: 0, MaxFps: 0, "ultrafast",
        DropAudio: false, AudioBitrateKbps: 128, AudioChannels: 0,
        UseTargetSize: false, TargetSizeMB: 0, UseTwoPass: false,
        DenoiseMode.Off, SharpenMode.Off, Deblock: false, Grayscale: false);

    // Reports on the encoding thread itself, unlike Progress<T>.
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
