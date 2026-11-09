using System.Diagnostics;
using GimmeCapture.Services.Core.Media;
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

    /// <summary>
    /// A preview of a video with pieces cut out: how long the picture stands still between one kept piece's last frame
    /// and the next one's first, in the one decode the editor plays them in.
    /// </summary>
    [OptInFact("EDITOR_PLAYBACK_CLIP")]
    public async Task ThePreviewOfACutVideo_StandsStillAtEachCut()
    {
        string clip = Environment.GetEnvironmentVariable("EDITOR_PLAYBACK_CLIP")!;
        using var prober = new LibavVideoFramePlayer();
        LibavVideoFramePlayer.MediaInfo info = await prober.ProbeAsync(clip);
        double scale = Math.Min(1.0, 720.0 / Math.Max(1, info.Width));
        int width = Math.Max(2, (int)(info.Width * scale)) & ~1;
        int height = Math.Max(2, (int)(info.Height * scale)) & ~1;

        // Pieces of a second; ten seconds, then a third of a second, between them.
        foreach (double between in new[] { 10.0, 0.3 })
        {
            var pieces = Enumerable.Range(0, 6).Select(i => (Start: 20.0 + (i * (1 + between)), End: 21.0 + (i * (1 + between)))).ToArray();
            output.WriteLine($"{between} s cut out: {string.Join(", ", (await StandingStillAtEachCut(clip, width, height, pieces)).Select(g => g.ToString("F0")))} ms");
        }

        output.WriteLine($"{Path.GetFileName(clip)}: a frame lasts {1000.0 / Math.Max(1, info.Fps):F0} ms");
    }

    private static async Task<List<double>> StandingStillAtEachCut(string clip, int width, int height, (double Start, double End)[] pieces)
    {
        var gaps = new List<double>();
        var clock = Stopwatch.StartNew();
        double lastFrameAt = -1;
        double lastSeconds = -1;
        using var decoder = new LibavStillFrameDecoder(clip, width, height);
        var run = new PlaybackPieces(pieces.Select(p => new PlaybackPiece(p.Start, p.End, 1.0)).ToArray(), 0, pieces[0].Start, () => false);
        await decoder.PlayPiecesAsync(run, (_, seconds) =>
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (lastSeconds >= 0 && seconds - lastSeconds > 0.2)
            {
                gaps.Add(now - lastFrameAt);
            }

            lastFrameAt = now;
            lastSeconds = seconds;
        }, CancellationToken.None);
        return gaps;
    }

    /// <summary>
    /// How long pressing Play takes to show a picture on a paused editor: a player of its own, which opens the file, the
    /// decoder and its GPU device and seeks, against the decoder the paused frame came from, in turn.
    /// </summary>
    [OptInFact("EDITOR_PLAYBACK_CLIP")]
    public async Task PressingPlay_Timed()
    {
        string clip = Environment.GetEnvironmentVariable("EDITOR_PLAYBACK_CLIP")!;
        using var prober = new LibavVideoFramePlayer();
        LibavVideoFramePlayer.MediaInfo info = await prober.ProbeAsync(clip);
        double scale = Math.Min(1.0, 720.0 / Math.Max(1, info.Width));
        int width = Math.Max(2, (int)(info.Width * scale)) & ~1;
        int height = Math.Max(2, (int)(info.Height * scale)) & ~1;
        double[] positions = [20.3, 41.7, 63.1, 88.45, 110.9, 132.2, 155.6, 180.05];

        using var kept = new LibavStillFrameDecoder(clip, width, height, TimeSpan.FromMinutes(5));
        await kept.DecodeFrameAtAsync(5);
        var ownPlayer = new List<double>();
        var keptDecoder = new List<double>();
        var keptResumed = new List<double>();
        for (int i = 0; i < positions.Length; i++)
        {
            // Places a few seconds apart, so neither way finds the file already in memory for the other.
            double forOwn = positions[i] + (i % 2 == 0 ? 0 : 7);
            double forKept = positions[i] + (i % 2 == 0 ? 7 : 0);
            await kept.DecodeFrameAtAsync(forKept - 3);
            for (int way = 0; way < 2; way++)
            {
                if ((way == 0) == (i % 2 == 0))
                {
                    ownPlayer.Add(await TimeToFirstFrame((frame, ct) =>
                    {
                        var player = new LibavVideoFramePlayer();
                        return player.PlayFramesAsync(clip, width, height, forOwn, 1.0, false, frame, ct).ContinueWith(
                            t =>
                            {
                                player.Dispose();
                                return t;
                            },
                            TaskScheduler.Default).Unwrap();
                    }));
                }
                else
                {
                    await kept.DecodeFrameAtAsync(forKept);
                    PlaybackPieces Pieces(double from) => new([new PlaybackPiece(0, info.Duration, 1.0)], 0, from, () => false);
                    keptDecoder.Add(await TimeToFirstFrame((frame, ct) => kept.PlayPiecesAsync(Pieces(forKept), frame, ct)));
                    keptResumed.Add(await TimeToFirstFrame((frame, ct) => kept.PlayPiecesAsync(Pieces(forKept + (4.0 / Math.Max(1, info.Fps))), frame, ct)));
                }

                await OwnThread.Delay(TimeSpan.FromMilliseconds(300));
            }
        }

        output.WriteLine($"{Path.GetFileName(clip)}, first frame after Play, in turn:");
        output.WriteLine($"  a player of its own  {string.Join(", ", ownPlayer.Select(v => v.ToString("F0")))} ms");
        output.WriteLine($"  the decoder kept     {string.Join(", ", keptDecoder.Select(v => v.ToString("F0")))} ms");
        output.WriteLine($"  the same, after a pause {string.Join(", ", keptResumed.Select(v => v.ToString("F0")))} ms");
    }

    // The milliseconds from starting to the first frame shown; the play is cancelled on it.
    private static async Task<double> TimeToFirstFrame(Func<LibavVideoFramePlayer.BgraFrameHandler, CancellationToken, Task> play)
    {
        using var stop = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        double first = -1;
        try
        {
            await play(
                (_, _) =>
                {
                    if (first < 0)
                    {
                        first = clock.Elapsed.TotalMilliseconds;
                        stop.Cancel();
                    }
                },
                stop.Token);
        }
        catch (OperationCanceledException)
        {
        }

        return first;
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
