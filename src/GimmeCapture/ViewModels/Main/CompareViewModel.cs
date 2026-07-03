using System;
using System.IO;
using System.Reactive;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Floating;
using ReactiveUI;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// Drives the inline side-by-side quality compare hosted in the editor preview. The scrubber spans the WHOLE
/// clip; the original (left) is decoded straight from the source at any position (instant), while the compressed
/// (right) is produced by encoding a short window that FOLLOWS the playhead on demand — so any point can be
/// compared quickly without encoding the whole (potentially 4K) file. Playback chains windows so it carries
/// through the clip. Both frames are rotated equally to match the editor's orientation.
/// </summary>
internal sealed class CompareViewModel : ViewModelBase, IDisposable
{
    private readonly string _sourcePath;
    private readonly LibavExportOptions _options;
    private readonly int _fps;
    private readonly int _decodeW;
    private readonly int _decodeH;
    private readonly double _duration;      // full source duration — the slider spans this
    private readonly int _rotation;         // applied to BOTH frames at display time so they match orientation
    private readonly double _anchorSeconds;  // initial window position (editor playhead); < 0 = ~10% in

    // Short encoded window that follows the playhead. Kept small so each (re)encode is fast, even for 4K.
    private const double WindowSeconds = 4.0;
    private readonly double _winLen;         // = clamp(WindowSeconds, 0.5, duration)

    private string? _sampleDir;
    private string? _samplePath;             // the currently-encoded window's temp file (null until first encode)
    private double _winStart = -1;           // start of the currently-encoded window (-1 = none yet)
    private int _sampleSeq;                  // unique temp-file name per window
    private readonly SemaphoreSlim _sampleLock = new(1, 1); // serialize encodes; latest request wins
    private CancellationTokenSource? _playCts;
    private CancellationTokenSource? _stepCts;
    private CancellationTokenSource? _sampleCts; // supersedes an in-flight window encode when the target moves

    internal CompareViewModel(
        string sourcePath, double durationSeconds, int fps, int sourceWidth, int sourceHeight,
        LibavExportOptions options, int rotation, double anchorSeconds = -1)
    {
        _sourcePath = sourcePath;
        _options = options;
        _fps = fps > 0 ? fps : 30;
        _rotation = ((rotation % 360) + 360) % 360;

        _duration = durationSeconds > 0 ? durationSeconds : WindowSeconds;
        _winLen = Math.Max(0.5, Math.Min(WindowSeconds, _duration));
        _anchorSeconds = anchorSeconds;

        int sw = sourceWidth > 0 ? sourceWidth : 1280;
        int sh = sourceHeight > 0 ? sourceHeight : 720;
        double scale = Math.Min(1.0, 720.0 / Math.Max(1, sw)); // cap decode width ~720px for responsiveness
        _decodeW = Math.Max(2, (int)(sw * scale)); _decodeW -= _decodeW & 1;
        _decodeH = Math.Max(2, (int)(sh * scale)); _decodeH -= _decodeH & 1;

        PlayPauseCommand = ReactiveCommand.CreateFromTask(TogglePlayAsync);
        StepBackCommand = ReactiveCommand.CreateFromTask(() => StepAsync(-1));
        StepForwardCommand = ReactiveCommand.CreateFromTask(() => StepAsync(1));
        SkipBackCommand = ReactiveCommand.CreateFromTask(() => SeekAsync(PositionSeconds - 5));
        SkipForwardCommand = ReactiveCommand.CreateFromTask(() => SeekAsync(PositionSeconds + 5));
        PlayPauseCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.ComparePlayPause", ex));
        StepBackCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.CompareStepBack", ex));
        StepForwardCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.CompareStepFwd", ex));
        SkipBackCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.CompareSkipBack", ex));
        SkipForwardCommand.ThrownExceptions.Subscribe(ex => AppLog.Error("Compress.CompareSkipFwd", ex));

        Title = Path.GetFileName(sourcePath);
        StatusText = LocalizationService.Instance["CompressComparePreparing"];
    }

    public string Title { get; }

    /// <summary>The scrubber's max — the whole source duration (the compare now spans the full clip).</summary>
    public double WindowLength => _duration;

    // The encoded window starts a little before the requested position (room to view/step around it), clamped so
    // the whole window still fits inside the clip.
    internal static double ComputeWindowStart(double durationSeconds, double windowLen, double pos)
        => Math.Clamp(pos - windowLen * 0.25, 0, Math.Max(0, durationSeconds - windowLen));

    private Bitmap? _sourceFrame;
    public Bitmap? SourceFrame { get => _sourceFrame; private set => this.RaiseAndSetIfChanged(ref _sourceFrame, value); }

    private Bitmap? _sampleFrame;
    public Bitmap? SampleFrame { get => _sampleFrame; private set => this.RaiseAndSetIfChanged(ref _sampleFrame, value); }

    private double _positionSeconds;
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            this.RaiseAndSetIfChanged(ref _positionSeconds, value);
            this.RaisePropertyChanged(nameof(TimeText));
        }
    }

    public string TimeText => $"{Fmt(_positionSeconds)} / {Fmt(_duration)}";

    private static string Fmt(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss");

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isPlaying, value);
            this.RaisePropertyChanged(nameof(PlayPauseGlyph));
        }
    }

    public string PlayPauseGlyph => _isPlaying ? "⏸" : "▶";

    // True while the compressed side is (re)sampling a window. The overlay is shown over the RIGHT panel only —
    // the source side stays navigable.
    private bool _isPreparing = true;
    public bool IsPreparing { get => _isPreparing; private set => this.RaiseAndSetIfChanged(ref _isPreparing, value); }

    private string _statusText = string.Empty;
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> StepBackCommand { get; }
    public ReactiveCommand<Unit, Unit> StepForwardCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipBackCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipForwardCommand { get; }

    /// <summary>Creates the temp dir and shows the first frame at the anchor position (which encodes the first
    /// window). Call once after the panel is shown.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _sampleDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compare_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sampleDir);

            double startPos = _anchorSeconds >= 0 ? Math.Clamp(_anchorSeconds, 0, _duration) : _duration * 0.1;
            await ShowFrameAtAsync(startPos);
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.CompareInit", ex);
            StatusText = LocalizationService.Instance["CompressQueueFailed"];
            IsPreparing = false;
        }
    }

    private async Task TogglePlayAsync()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }

        await PlayAsync();
    }

    private Task PlayAsync()
    {
        _stepCts?.Cancel();
        _playCts?.Cancel();
        _playCts = new CancellationTokenSource();
        CancellationToken ct = _playCts.Token;
        IsPlaying = true;

        double start = PositionSeconds >= _duration - 0.05 ? 0 : PositionSeconds; // replay from start if at end
        PositionSeconds = start;

        _ = Task.Run(() => PlayLoopAsync(start, ct), ct);
        return Task.CompletedTask;
    }

    // Plays source + sample in sync, chaining short windows so playback follows the playhead through the whole
    // clip (with a brief re-sample at each window boundary).
    private async Task PlayLoopAsync(double startPos, CancellationToken ct)
    {
        try
        {
            double pos = startPos;
            while (!ct.IsCancellationRequested && pos < _duration - 0.02)
            {
                if (!await EnsureWindowAsync(pos) || ct.IsCancellationRequested)
                {
                    break;
                }

                double winStart = _winStart;
                double winEnd = Math.Min(_duration, winStart + _winLen);
                string samplePath = _samplePath!;

                using var winCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                CancellationToken wct = winCts.Token;

                // Compressed (right): play this window [pos-winStart, winLen].
                var sampleTask = Task.Run(async () =>
                {
                    try
                    {
                        using var ps = new LibavVideoFramePlayer();
                        await ps.PlayAsync(samplePath, _decodeW, _decodeH, pos - winStart, 1.0, false, (data, _) =>
                        {
                            if (wct.IsCancellationRequested) throw new OperationCanceledException();
                            Bitmap? bmp = BytesToBitmap(data);
                            Dispatcher.UIThread.Post(() => SampleFrame = bmp);
                        }, wct);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { AppLog.Error("Compress.ComparePlaySample", ex); }
                }, wct);

                // Original (left): play [pos, winEnd]; stop at the window end.
                try
                {
                    using var pp = new LibavVideoFramePlayer();
                    await pp.PlayAsync(_sourcePath, _decodeW, _decodeH, pos, 1.0, false, (data, ts) =>
                    {
                        if (wct.IsCancellationRequested || ts >= winEnd) throw new OperationCanceledException();
                        Bitmap? bmp = BytesToBitmap(data);
                        Dispatcher.UIThread.Post(() => { SourceFrame = bmp; PositionSeconds = ts; });
                    }, wct);
                }
                catch (OperationCanceledException) { /* window end or paused */ }

                winCts.Cancel();                 // stop the sample player for this window
                try { await sampleTask; } catch { /* already stopping */ }

                if (ct.IsCancellationRequested)
                {
                    break;                       // paused by the user
                }

                pos = winEnd;                    // advance to the next window
                double next = pos;
                Dispatcher.UIThread.Post(() => PositionSeconds = next);
            }
        }
        catch (OperationCanceledException) { /* paused */ }
        catch (Exception ex) { AppLog.Error("Compress.ComparePlay", ex); }
        finally { Dispatcher.UIThread.Post(() => IsPlaying = false); }
    }

    private void Pause()
    {
        _playCts?.Cancel();
        IsPlaying = false;
    }

    // Slider scrubbing (driven by the panel code-behind): pause on grab, seek to the dropped position on release.
    public void BeginScrub() => Pause();

    public async Task SeekAsync(double pos)
    {
        Pause();
        await ShowFrameAtAsync(pos);
    }

    private async Task StepAsync(int direction)
    {
        Pause();
        await ShowFrameAtAsync(PositionSeconds + direction * (1.0 / _fps));
    }

    // Shows the source frame immediately (no encode), then ensures a compressed window covering `pos` and shows
    // the sample frame at the in-window offset — so full-length navigation stays instant on the original side.
    private async Task ShowFrameAtAsync(double pos)
    {
        pos = Math.Clamp(pos, 0, _duration);
        PositionSeconds = pos;

        _stepCts?.Cancel();
        _stepCts = new CancellationTokenSource();
        CancellationToken ct = _stepCts.Token;

        try
        {
            byte[]? src = await LibavVideoFramePlayer.DecodeFrameAtAsync(_sourcePath, pos, _decodeW, _decodeH, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
            if (src != null) { SourceFrame = BytesToBitmap(src); }

            bool ready = await EnsureWindowAsync(pos);
            if (ct.IsCancellationRequested || !ready || _samplePath == null)
            {
                return;
            }

            byte[]? smp = await LibavVideoFramePlayer.DecodeFrameAtAsync(_samplePath, pos - _winStart, _decodeW, _decodeH, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
            if (smp != null) { SampleFrame = BytesToBitmap(smp); }
        }
        catch (OperationCanceledException) { /* superseded by a newer seek/step */ }
        catch (Exception ex) { AppLog.Error("Compress.CompareShowFrame", ex); }
    }

    // Ensures the compressed sample covers `pos`, re-encoding a short window around it if needed. Supersedes any
    // in-flight encode (rapid seeks coalesce to the latest). Returns true when a window covering `pos` is ready.
    private async Task<bool> EnsureWindowAsync(double pos)
    {
        if (_sampleDir == null)
        {
            return false;
        }

        if (Covers(pos))
        {
            return true;
        }

        _sampleCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sampleCts = cts;
        CancellationToken ct = cts.Token;

        try
        {
            await _sampleLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false; // superseded while waiting
        }

        try
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }
            if (Covers(pos))
            {
                return true; // a prior waiter already produced a covering window
            }

            double winStart = ComputeWindowStart(_duration, _winLen, pos);
            string newPath = Path.Combine(_sampleDir, $"win_{++_sampleSeq}.mp4");
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = LocalizationService.Instance["CompressComparePreparing"];
                IsPreparing = true;
            });

            var ranges = new[] { new LibavClipExporter.SourceRange(winStart, winStart + _winLen) };
            bool ok = await Task.Run(() => LibavClipExporter.TryExport(
                _sourcePath, ranges, newPath, VideoQuality.Medium, cancellationToken: ct, options: _options), ct)
                .ConfigureAwait(false);

            if (ct.IsCancellationRequested)
            {
                TryDelete(newPath);
                return false;
            }

            if (!ok || !File.Exists(newPath) || new FileInfo(newPath).Length == 0)
            {
                TryDelete(newPath);
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = LocalizationService.Instance["CompressQueueFailed"];
                    IsPreparing = false;
                });
                return false;
            }

            string? old = _samplePath;
            _samplePath = newPath;
            _winStart = winStart;
            if (old != null) { TryDelete(old); }

            Dispatcher.UIThread.Post(() => { StatusText = string.Empty; IsPreparing = false; });
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _sampleLock.Release();
        }
    }

    private bool Covers(double pos)
        => _samplePath != null && _winStart >= 0 && pos >= _winStart - 0.001 && pos <= _winStart + _winLen + 0.001;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
        catch { /* best-effort; the whole temp dir is removed on Dispose */ }
    }

    private Bitmap? BytesToBitmap(byte[] bgra)
    {
        try
        {
            using var sk = new SKBitmap(new SKImageInfo(_decodeW, _decodeH, SKColorType.Bgra8888, SKAlphaType.Premul));
            Marshal.Copy(bgra, 0, sk.GetPixels(), Math.Min(bgra.Length, _decodeW * _decodeH * 4));
            if (!FloatingBitmapConversionHelper.TryCreateDetachedBitmapFromSkBitmap(sk, out Bitmap? bmp, out _) || bmp == null)
            {
                return null;
            }

            if (_rotation == 0)
            {
                return bmp;
            }

            // Both source and sample are rotated equally here, so they match the editor's chosen orientation.
            Bitmap? rotated = FloatingBitmapConversionHelper.TransformBitmap(bmp, _rotation, false, false);
            bmp.Dispose();
            return rotated;
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.CompareFrameBuild", ex);
            return null;
        }
    }

    public void Dispose()
    {
        _playCts?.Cancel();
        _stepCts?.Cancel();
        _sampleCts?.Cancel();
        _playCts?.Dispose();
        _stepCts?.Dispose();
        _sampleCts?.Dispose();
        _sampleLock.Dispose();
        try
        {
            if (_sampleDir != null && Directory.Exists(_sampleDir))
            {
                Directory.Delete(_sampleDir, true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.CompareCleanup", ex);
        }
    }
}
