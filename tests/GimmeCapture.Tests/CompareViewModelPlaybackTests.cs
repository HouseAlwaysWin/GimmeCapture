using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Main;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// A quality compare's playback, through its view model on a real decoder and a headless Avalonia application, which a
/// process can set up only once: each test is opt-in by a switch of its own.
/// </summary>
public sealed class CompareViewModelPlaybackTests(ITestOutputHelper output) : IDisposable
{
    private const int Width = 64;
    private const int Height = 48;
    private const int Fps = 30;
    private const double Duration = 20;

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Play starts on the window already encoded, with nothing shown over the picture, while the one after it is encoded;
    /// the overlay comes only if the picture reaches a window not ready yet, and playing goes on when it is.
    /// </summary>
    [OptInFact("COMPARE_PLAY_PROBE=1")]
    public void Play_StartsOnWhatIsEncoded_AndWaitsOnlyForAWindowNotReady()
    {
        SetUpAvalonia();
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, (int)(Duration * Fps), Fps, gopSize: 10);
        using var release = new ManualResetEventSlim();
        var asked = new List<double>();
        using var decoder = new LibavStillFrameDecoder(clip, Width, Height);

        // Past the first window, the encoder waits to be let go.
        var vm = new CompareViewModel(decoder, Duration, Fps, new LibavExportOptions(), 0, anchorSeconds: 4, encodeWindow: (start, length, path, ct) =>
        {
            lock (asked)
            {
                asked.Add(start);
            }

            if (start > 3)
            {
                release.Wait(ct);
            }

            VideoTestClips.WriteConstantRate(path, Width, Height, (int)(length * Fps), Fps, gopSize: 10);
            return true;
        });

        Task opening = vm.InitializeAsync();
        Pump(() => opening.IsCompleted && !vm.IsPreparing, "the first window");
        Assert.NotNull(vm.SourceFrame);
        Assert.NotNull(vm.SampleFrame);
        int firstSample = Hash(vm.SampleFrame!);

        vm.PlayPauseCommand.Execute().Subscribe();
        Assert.True(vm.IsPlaying);
        bool sampleMoved = false;
        Pump(
            () =>
            {
                Assert.False(vm.IsPreparing);
                sampleMoved |= Hash(vm.SampleFrame!) != firstSample;
                return sampleMoved && vm.PositionSeconds > 5;
            },
            "the picture to move");

        // The first window runs from 2.5 to 8.5; what follows it has been asked for, and is not there.
        lock (asked)
        {
            Assert.Equal(new[] { 2.5, 8.5 }, asked);
        }

        Pump(() => vm.IsPreparing, "the end of what is encoded");
        Assert.True(vm.IsPlaying);
        release.Set();
        Pump(() => !vm.IsPreparing && vm.PositionSeconds > 9, "the next window");
        Assert.True(vm.IsPlaying);

        vm.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// A seek to a place not encoded keeps the overlay on the compressed side until its frame is there, or the picture from
    /// before the seek shows uncovered while the window's decoder opens.
    /// </summary>
    [OptInFact("COMPARE_SEEK_PROBE=1")]
    public void ASeekToAPlaceNotEncoded_KeepsTheOverlay_UntilItsFrameIsThere()
    {
        SetUpAvalonia();
        string clip = VideoTestClips.WriteConstantRate(Path.Combine(_dir, "clip.mp4"), Width, Height, (int)(Duration * Fps), Fps, gopSize: 10);
        using var release = new ManualResetEventSlim();
        using var decoder = new LibavStillFrameDecoder(clip, Width, Height);
        var vm = new CompareViewModel(decoder, Duration, Fps, new LibavExportOptions(), 0, anchorSeconds: 4, encodeWindow: (start, length, path, ct) =>
        {
            if (start > 3)
            {
                release.Wait(ct);
            }

            VideoTestClips.WriteConstantRate(path, Width, Height, (int)(length * Fps), Fps, gopSize: 10);
            return true;
        });

        Task opening = vm.InitializeAsync();
        Pump(() => opening.IsCompleted && !vm.IsPreparing, "the first window");
        int before = Hash(vm.SampleFrame!);

        // Near the end, where the window starts further before the time than it does elsewhere: another frame of a window.
        Task seek = vm.SeekAsync(Duration - 0.1);
        Pump(() => vm.IsPreparing, "the overlay");
        release.Set();
        Pump(() => !vm.IsPreparing, "the end of the wait");
        Assert.NotEqual(before, Hash(vm.SampleFrame!));

        Pump(() => seek.IsCompleted, "the seek");
        vm.Dispose();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// What the compare does with a real video and the real encoder: how soon each side shows, what pressing Play takes, and
    /// where the picture stops. EDITOR_PLAYBACK_CLIP names the video, COMPARE_PROBE_SETTINGS is h264 (480p) or av1 (1080p),
    /// both at slow, and COMPARE_PROBE_SECONDS how long to watch it play.
    /// </summary>
    [OptInFact("COMPARE_REAL_PROBE=1", "EDITOR_PLAYBACK_CLIP")]
    public void ARealVideo_Timed()
    {
        string clip = Environment.GetEnvironmentVariable("EDITOR_PLAYBACK_CLIP")!;
        bool av1 = Environment.GetEnvironmentVariable("COMPARE_PROBE_SETTINGS") == "av1";
        double watchFor = double.TryParse(Environment.GetEnvironmentVariable("COMPARE_PROBE_SECONDS"), out double s) ? s : 25;
        LibavVideoFramePlayer.MediaInfo info = Task.Run(async () =>
        {
            using var prober = new LibavVideoFramePlayer();
            return await prober.ProbeAsync(clip);
        }).GetAwaiter().GetResult();

        SetUpAvalonia(); // no await after this: the UI thread is this one
        VideoCodec codec = av1 ? VideoCodec.Av1 : VideoCodec.H264;
        LibavExportOptions options = new CompressSettingsSnapshot(
            codec, CompressEncodeMath.EncoderScaleCrf(codec, 23), av1 ? 1080 : 480, 0, "slow", false, 0, 2, false, 25m, false,
            DenoiseMode.Off, SharpenMode.Off, false, false).ToExportOptions();
        double scale = Math.Min(1.0, 720.0 / Math.Max(1, info.Width));
        int width = Math.Max(2, (int)(info.Width * scale)) & ~1;
        int height = Math.Max(2, (int)(info.Height * scale)) & ~1;
        using var decoder = new LibavStillFrameDecoder(clip, width, height, TimeSpan.FromMinutes(5));

        var watch = Stopwatch.StartNew();
        var vm = new CompareViewModel(decoder, info.Duration, info.Fps, options, 0, anchorSeconds: 60, encodeWindow: (start, length, path, ct) =>
        {
            double began = watch.Elapsed.TotalSeconds;
            bool ok = LibavClipExporter.TryExport(
                clip, [new LibavClipExporter.SourceRange(start, start + length)], path, VideoQuality.Medium, cancellationToken: ct, options: options);
            output.WriteLine($"  [{watch.Elapsed.TotalSeconds,6:F2}] window {start:F1}+{length:F1} encoded in {watch.Elapsed.TotalSeconds - began:F2} s, ok={ok}");
            return ok;
        });

        Task opening = vm.InitializeAsync();
        Pump(() => vm.SourceFrame != null, "the source side");
        output.WriteLine($"source side at {watch.ElapsedMilliseconds} ms");
        Pump(() => opening.IsCompleted && !vm.IsPreparing && vm.SampleFrame != null, "the compressed side");
        output.WriteLine($"compressed side at {watch.ElapsedMilliseconds} ms");

        var source = new SideWatch(vm.SourceFrame!);
        var sample = new SideWatch(vm.SampleFrame!);
        watch.Restart();
        vm.PlayPauseCommand.Execute().Subscribe();
        bool waiting = false;
        var deadline = TimeSpan.FromSeconds(watchFor);
        while (watch.Elapsed < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            source.Look(vm.SourceFrame!, watch.Elapsed.TotalMilliseconds);
            sample.Look(vm.SampleFrame!, watch.Elapsed.TotalMilliseconds);
            if (vm.IsPreparing != waiting)
            {
                waiting = vm.IsPreparing;
                output.WriteLine($"  [{watch.Elapsed.TotalSeconds,6:F2}] overlay {(waiting ? "on" : "off")}, position {vm.PositionSeconds:F2}");
            }

            Thread.Sleep(1);
        }

        vm.Dispose();
        Dispatcher.UIThread.RunJobs();
        output.WriteLine($"source: first frame +{source.First:F0} ms, {source.Changes / watchFor:F1} a second, gaps over 120 ms: {source.Gaps()}");
        output.WriteLine($"sample: first frame +{sample.First:F0} ms, {sample.Changes / watchFor:F1} a second, gaps over 120 ms: {sample.Gaps()}");
    }

    private static int Hash(Bitmap bitmap)
    {
        using var buffer = ((WriteableBitmap)bitmap).Lock();
        var row = new byte[buffer.Size.Width * 4];
        Marshal.Copy(buffer.Address + (buffer.RowBytes * (buffer.Size.Height / 2)), row, 0, row.Length);
        var hash = default(HashCode);
        hash.AddBytes(row);
        return hash.ToHashCode();
    }

    private static void SetUpAvalonia() =>
        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

    private static void Pump(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
    }

    private sealed class SideWatch(Bitmap first)
    {
        private readonly List<(double At, double Length)> _gaps = [];
        private int _hash = Hash(first);
        private double _last = -1;

        public double First { get; private set; } = -1;

        public int Changes { get; private set; }

        public void Look(Bitmap bitmap, double milliseconds)
        {
            int hash = Hash(bitmap);
            if (hash == _hash)
            {
                return;
            }

            _hash = hash;
            Changes++;
            if (_last < 0)
            {
                First = milliseconds;
            }
            else if (milliseconds - _last > 120)
            {
                _gaps.Add((_last, milliseconds - _last));
            }

            _last = milliseconds;
        }

        public string Gaps() => string.Join(", ", _gaps.Select(g => $"{g.Length:F0} at {g.At / 1000:F1}s"));
    }
}
