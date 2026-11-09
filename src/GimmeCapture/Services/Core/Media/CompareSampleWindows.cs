using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.Media;

/// <summary>A stretch of the video encoded with the settings being compared: its file, and its place in the video.</summary>
internal sealed record CompareSampleWindow(string Path, double Start, double Length)
{
    public double End => Start + Length;
}

/// <summary>
/// The stretches of a video that a quality compare has encoded, so that any point of a long video can be compared without
/// encoding all of it. One encodes at a time: a request for another place cancels the one under way, a request for the
/// place it is encoding waits for it. The last few are kept for a playhead that comes back, in a folder of their own
/// that goes when the compare closes.
/// </summary>
internal sealed class CompareSampleWindows : IDisposable
{
    /// <summary>Encodes the stretch into the file; false when that failed or was cancelled.</summary>
    internal delegate bool EncodeWindow(double start, double length, string path, CancellationToken ct);

    /// <summary>A window for a seek or to begin on: quick to show, with room to step around.</summary>
    internal const double ShortSeconds = 6;

    /// <summary>The longest window encoded ahead of a playback, for an encoder far ahead of the picture.</summary>
    internal const double LongSeconds = 30;

    /// <summary>How many are kept: the one under the playhead, the next, and the one it just left.</summary>
    internal const int KeptWindows = 3;

    // The shortest file made, however little of the video is left.
    private const double ShortestSeconds = 0.5;

    // A time this near a window's end has nothing left to play in it.
    private const double EndMargin = 0.05;

    private const double Tolerance = 0.001;

    private readonly double _videoSeconds;
    private readonly EncodeWindow _encode;
    private readonly object _lock = new();
    private readonly List<CompareSampleWindow> _windows = []; // oldest first
    private Encoding? _underWay;
    private string? _folder;
    private int _made;
    private double _encodedPerSecond; // of video, by the last encode; 0 before the first
    private bool _disposed;

    public CompareSampleWindows(double videoSeconds, EncodeWindow encode)
    {
        _videoSeconds = videoSeconds;
        _encode = encode;
    }

    private sealed record Encoding(double Start, double Length, CancellationTokenSource Cancel)
    {
        public Task<CompareSampleWindow?> Done { get; set; } = Task.FromResult<CompareSampleWindow?>(null);
    }

    /// <summary>Makes the folder the windows go in. Nothing is encoded before this.</summary>
    public void Open()
    {
        string folder = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compare_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        lock (_lock)
        {
            _folder = folder;
        }
    }

    /// <summary>The window to play from at this time, if one has been encoded with something left to play in it.</summary>
    public CompareSampleWindow? PlayableAt(double seconds)
    {
        lock (_lock)
        {
            return _windows.LastOrDefault(w => HasLeftToPlay(w.Start, w.Length, seconds));
        }
    }

    /// <summary>
    /// A window with this time in it, for a paused frame: one encoded or being encoded, or else a short one encoded now that
    /// starts a little before it. Null when it failed or something newer took its place.
    /// </summary>
    public Task<CompareSampleWindow?> AroundAsync(double seconds)
    {
        lock (_lock)
        {
            if (_windows.LastOrDefault(w => Holds(w.Start, w.Length, seconds)) is { } encoded)
            {
                return Task.FromResult<CompareSampleWindow?>(encoded);
            }

            if (_underWay is { } encoding && Holds(encoding.Start, encoding.Length, seconds))
            {
                return encoding.Done;
            }
        }

        double length = Math.Min(ShortSeconds, _videoSeconds);
        return Begin(WindowStartAround(_videoSeconds, length, seconds), length);
    }

    /// <summary>
    /// A window to play from at this time: one encoded or being encoded, or else a short one encoded now that starts there.
    /// Null when it failed or something newer took its place.
    /// </summary>
    public Task<CompareSampleWindow?> ToPlayFromAsync(double seconds)
    {
        lock (_lock)
        {
            if (_windows.LastOrDefault(w => HasLeftToPlay(w.Start, w.Length, seconds)) is { } encoded)
            {
                return Task.FromResult<CompareSampleWindow?>(encoded);
            }

            if (_underWay is { } encoding && HasLeftToPlay(encoding.Start, encoding.Length, seconds))
            {
                return encoding.Done;
            }
        }

        return Begin(seconds, Math.Min(ShortSeconds, _videoSeconds - seconds));
    }

    /// <summary>
    /// Starts on what follows the window playing now, unless that is encoded or under way: as much as can be ready when
    /// the <paramref name="secondsToPlay"/> left of it are over, going by the last encode's speed. Gives that window once
    /// it is there; null when nothing follows, it failed or something newer took its place.
    /// </summary>
    public Task<CompareSampleWindow?> EncodeAhead(double start, double secondsToPlay)
    {
        double left = _videoSeconds - start;
        if (left < EndMargin)
        {
            return Task.FromResult<CompareSampleWindow?>(null);
        }

        double speed;
        lock (_lock)
        {
            if (_windows.LastOrDefault(w => HasLeftToPlay(w.Start, w.Length, start)) is { } encoded)
            {
                return Task.FromResult<CompareSampleWindow?>(encoded);
            }

            speed = _encodedPerSecond;
        }

        return Begin(start, Math.Min(AheadSeconds(secondsToPlay, speed), left));
    }

    /// <summary>
    /// How much to encode while <paramref name="secondsToPlay"/> of the present window play: what is ready by their end, with
    /// a margin, for an encoder doing <paramref name="encodedPerSecond"/> seconds of video a second. At least a short
    /// window, since a shorter one would only stop the picture more often.
    /// </summary>
    internal static double AheadSeconds(double secondsToPlay, double encodedPerSecond) =>
        Math.Clamp(secondsToPlay * encodedPerSecond * 0.8, ShortSeconds, LongSeconds);

    /// <summary>
    /// Where a window of this length around the time starts: a little before it, for room to step back, and with the
    /// whole window inside the video.
    /// </summary>
    internal static double WindowStartAround(double videoSeconds, double length, double seconds) =>
        Math.Clamp(seconds - (length * 0.25), 0, Math.Max(0, videoSeconds - length));

    private static bool Holds(double start, double length, double seconds) =>
        seconds >= start - Tolerance && seconds <= start + length + Tolerance;

    private static bool HasLeftToPlay(double start, double length, double seconds) =>
        seconds >= start - Tolerance && seconds < start + length - EndMargin;

    // The latest request wins: it cancels the encode under way and runs once that has stopped.
    private Task<CompareSampleWindow?> Begin(double start, double length)
    {
        length = Math.Max(ShortestSeconds, Math.Min(length, _videoSeconds));
        start = Math.Clamp(start, 0, Math.Max(0, _videoSeconds - length));
        lock (_lock)
        {
            if (_disposed || _folder == null)
            {
                return Task.FromResult<CompareSampleWindow?>(null);
            }

            if (_underWay is { } same && Math.Abs(same.Start - start) < 0.01)
            {
                return same.Done;
            }

            Encoding? replaced = _underWay;
            replaced?.Cancel.Cancel();
            var encoding = new Encoding(start, length, new CancellationTokenSource());
            string path = Path.Combine(_folder, $"win_{++_made}.mp4");
            encoding.Done = EncodeAfterAsync(replaced?.Done, encoding, path);
            _underWay = encoding;
            return encoding.Done;
        }
    }

    // Never throws, so that the encode after it can wait for it.
    private async Task<CompareSampleWindow?> EncodeAfterAsync(Task<CompareSampleWindow?>? before, Encoding encoding, string path)
    {
        if (before != null)
        {
            await before.ConfigureAwait(false);
        }

        CancellationToken ct = encoding.Cancel.Token;
        var watch = Stopwatch.StartNew();
        bool encoded = false;
        try
        {
            encoded = !ct.IsCancellationRequested
                && await Task.Run(() => _encode(encoding.Start, encoding.Length, path, ct), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.CompareEncode", ex);
        }

        double seconds = watch.Elapsed.TotalSeconds;
        bool usable = encoded && !ct.IsCancellationRequested && File.Exists(path) && new FileInfo(path).Length > 0;
        lock (_lock)
        {
            if (ReferenceEquals(_underWay, encoding))
            {
                _underWay = null;
            }

            if (usable && !_disposed)
            {
                var window = new CompareSampleWindow(path, encoding.Start, encoding.Length);
                _encodedPerSecond = encoding.Length / Math.Max(0.001, seconds);
                _windows.Add(window);
                if (_windows.Count > KeptWindows)
                {
                    TryDelete(_windows[0].Path);
                    _windows.RemoveAt(0);
                }

                return window;
            }
        }

        TryDelete(path);
        return null;
    }

    // A decoder may still hold it: the folder goes when the compare closes.
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Debug("Compress.CompareWindowDelete", ex);
        }
    }

    /// <summary>Stops the encode under way, and removes the folder once it has.</summary>
    public void Dispose()
    {
        string? folder;
        Task underWay;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            folder = _folder;
            underWay = _underWay?.Done ?? Task.CompletedTask;
            _underWay?.Cancel.Cancel();
            _windows.Clear();
        }

        if (folder != null)
        {
            underWay.ContinueWith(_ => DeleteFolder(folder), TaskScheduler.Default).Forget("Compress.CompareCleanup");
        }
    }

    // The decoders that were reading it may take a moment to let go.
    private static void DeleteFolder(string folder)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 20)
                {
                    AppLog.Warning("Compress.CompareCleanup", ex);
                    return;
                }

                Thread.Sleep(100);
            }
        }
    }
}
