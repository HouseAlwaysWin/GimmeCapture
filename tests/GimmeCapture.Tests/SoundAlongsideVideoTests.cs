using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.Tests;

/// <summary>
/// An export builds its sound while its video encodes, where it used to build it after, with the bar at 100%: a single
/// export (<see cref="LibavClipExporter.TryExport"/>) and the segmented compress, whose chunks encode one by one while
/// the whole span's sound builds. Each test pauses the encode at its first report and finds the sound's file already
/// in the export's temporary folder, and not growing: the sound has started, and waits with the video. Before, no
/// sound file existed until the video was done. Real libav on tiny clips with a two-minute tone (<see
/// cref="VideoTestClips"/>), whose sound takes a second or two to build. They run on their own (<see
/// cref="ExportTempFolders"/>): another test's export would make folders of the same kind meanwhile.
/// </summary>
[Collection(ExportTempFolders.Name)]
public sealed class SoundAlongsideVideoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public SoundAlongsideVideoTests()
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
    public async Task AnExport_BuildsItsSound_WhileTheVideoEncodes_AndPausesIt()
    {
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, 3600, 30);
        string output = Path.Combine(_dir, "out.mp4");
        var folders = new NewFolders("GimmeCapture_ClipExport_*");
        using var pause = new ManualResetEventSlim(true);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bar = new InlineProgress(_ =>
        {
            if (pause.IsSet && !paused.Task.IsCompleted)
            {
                pause.Reset();
                paused.TrySetResult();
            }
        });

        Task<bool> export = Task.Run(() => LibavClipExporter.TryExport(
            clip, [new LibavClipExporter.SourceRange(0, 120)], output, VideoQuality.Medium,
            options: new LibavExportOptions { Preset = "ultrafast" }, progress: bar, pauseGate: pause));

        await AssertTheSoundHasStarted_AndWaitsAsync(paused.Task, folders, pause, export);
        Assert.True(await export);
        using var player = new LibavVideoFramePlayer();
        Assert.True(await player.ProbeHasAudioAsync(output));
    }

    [Fact]
    public async Task ASegmentedCompress_BuildsTheSpansSound_WhileItsChunksEncode_AndPausesIt()
    {
        string clip = VideoTestClips.WriteWithTone(Path.Combine(_dir, "clip.mkv"), 64, 48, 3600, 30);
        string output = Path.Combine(_dir, "out.mp4");
        var folders = new NewFolders("GimmeCapture_Compress_*");
        using var pause = new ManualResetEventSlim(true);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bar = new InlineProgress(value =>
        {
            if (value > 0 && pause.IsSet && !paused.Task.IsCompleted)
            {
                pause.Reset();
                paused.TrySetResult();
            }
        });

        Task<bool> compress = CompressPipeline.EncodeOneFileAsync(
            clip, output, Settings(), 0, 120, bar, CancellationToken.None, pause);

        await AssertTheSoundHasStarted_AndWaitsAsync(paused.Task, folders, pause, compress);
        Assert.True(await compress);
        using var player = new LibavVideoFramePlayer();
        Assert.True(await player.ProbeHasAudioAsync(output));
    }

    // Once the pause has had time to reach both: the sound's WAV (or, already encoded, its AAC) is in the export's
    // folder, and its size stays the same. Then the export resumes and is let finish, whatever was found, so that it
    // is not left waiting with the test's clip open.
    private static async Task AssertTheSoundHasStarted_AndWaitsAsync(
        Task paused, NewFolders folders, ManualResetEventSlim pause, Task encoding)
    {
        try
        {
            await paused.WaitAsync(TimeSpan.FromSeconds(30));
            long settled = await SettledAsync(() => SoundBytes(folders));
            await OwnThread.Delay(TimeSpan.FromMilliseconds(500));

            Assert.True(settled >= 0, "no sound file while the video was being encoded");
            Assert.Equal(settled, SoundBytes(folders));
        }
        finally
        {
            pause.Set();
            await Task.WhenAny(encoding, Task.Delay(TimeSpan.FromSeconds(30)));
        }
    }

    // The size of the sound file being built in a folder the export made: -1 while there is none.
    private static long SoundBytes(NewFolders folders)
    {
        foreach (string folder in folders.Made())
        {
            foreach (string name in new[] { "audio.wav", "audio.m4a" })
            {
                var file = new FileInfo(Path.Combine(folder, name));
                if (file.Exists)
                {
                    return file.Length;
                }
            }
        }

        return -1;
    }

    // The value once it has stayed the same for 300 ms, or where it is after 10 s.
    private static async Task<long> SettledAsync(Func<long> value)
    {
        var waited = Stopwatch.StartNew();
        long last = value();
        while (waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            await OwnThread.Delay(TimeSpan.FromMilliseconds(300));
            long now = value();
            if (now == last)
            {
                return now;
            }

            last = now;
        }

        return last;
    }

    private static CompressSettingsSnapshot Settings() => new(
        VideoCodec.H264, 28, MaxHeight: 0, MaxFps: 0, "ultrafast",
        DropAudio: false, AudioBitrateKbps: 128, AudioChannels: 0,
        UseTargetSize: false, TargetSizeMB: 0, UseTwoPass: false,
        DenoiseMode.Off, SharpenMode.Off, Deblock: false, Grayscale: false);

    // The temporary folders matching a pattern that were made after this was.
    private sealed class NewFolders(string pattern)
    {
        private readonly HashSet<string> _before = Folders(pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Made() => Folders(pattern).Where(folder => !_before.Contains(folder));

        private static IEnumerable<string> Folders(string pattern) =>
            Directory.EnumerateDirectories(Path.GetTempPath(), pattern);
    }

    // Reports on the encoding thread itself, unlike Progress<T>.
    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

/// <summary>
/// Tests that look for the files an export makes in the temporary folder, by kind of folder or by name: they run on their
/// own, after the others, as another test's export would make the same kinds meanwhile.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExportTempFolders
{
    public const string Name = "Export temporary folders";
}
