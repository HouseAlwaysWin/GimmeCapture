using System.Diagnostics;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// What the Compress editor's preview takes while it plays: the video decoded at the editor's width of 720, as its
/// view model asks, frames counted against the seconds played and each thread's CPU cycles read. Opt-in
/// (EDITOR_PLAYBACK_CLIP names a video; EDITOR_PLAYBACK_START the second to start at, 30 by default).
/// </summary>
public sealed class EditorPlaybackProbeTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Played = TimeSpan.FromSeconds(12);

    [OptInFact("EDITOR_PLAYBACK_CLIP")]
    public async Task ThePreviewOfAVideo_Timed()
    {
        string clip = Environment.GetEnvironmentVariable("EDITOR_PLAYBACK_CLIP")!;
        double start = double.TryParse(Environment.GetEnvironmentVariable("EDITOR_PLAYBACK_START"), out double s) ? s : 30;
        using var prober = new LibavVideoFramePlayer();
        LibavVideoFramePlayer.MediaInfo info = await prober.ProbeAsync(clip);
        double scale = Math.Min(1.0, 720.0 / Math.Max(1, info.Width));
        int width = Math.Max(2, (int)(info.Width * scale)) & ~1;
        int height = Math.Max(2, (int)(info.Height * scale)) & ~1;
        output.WriteLine($"{Path.GetFileName(clip)}: {info.Width}x{info.Height} {info.Fps} fps, decoded at {width}x{height}, from {start} s");

        // A first run warms the file cache and the JIT.
        await Play(clip, width, height, start, TimeSpan.FromSeconds(3));

        int frames = 0;
        var rows = ThreadActivity.Measure(() => frames = Play(clip, width, height, start, Played).GetAwaiter().GetResult());
        output.WriteLine($"{frames / Played.TotalSeconds:F1} frames shown a second (the video has {info.Fps}); {rows.Sum(r => r.MegaCycles):F0} M cycles/s");
        foreach (var row in rows.OrderByDescending(r => r.MegaCycles).Take(6))
        {
            output.WriteLine($"  {row.MegaCycles,8:F1} M cycles/s {row.Wakeups,8:F1} wakeups/s  {ThreadActivity.Describe(row.Thread)}");
        }
    }

    private static async Task<int> Play(string clip, int width, int height, double start, TimeSpan duration)
    {
        using var player = new LibavVideoFramePlayer();
        using var stop = new CancellationTokenSource();
        var stopAt = OwnThread.Run(() =>
        {
            Thread.Sleep(duration);
            stop.Cancel();
        });
        int frames = 0;
        try
        {
            await player.PlayAsync(clip, width, height, start, 1.0, false, (data, ts) => frames++, stop.Token);
        }
        catch (OperationCanceledException)
        {
        }

        await stopAt;
        return frames;
    }
}
