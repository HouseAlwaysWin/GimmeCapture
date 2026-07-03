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
/// Drives the side-by-side quality-compare window: encodes a short sample of the source with the chosen
/// settings, then plays the original and the compressed sample in sync, with pause + frame-by-frame stepping
/// so the user can judge quality loss by eye. Original plays the source window [t0, t1]; the sample plays
/// [0, t1-t0], synced by relative position.
/// </summary>
internal sealed class CompareViewModel : ViewModelBase, IDisposable
{
    private readonly string _sourcePath;
    private readonly LibavExportOptions _options;
    private readonly int _fps;
    private readonly int _decodeW;
    private readonly int _decodeH;
    private readonly double _t0;        // source window start (seconds)
    private readonly double _windowLen; // window length (seconds)
    private readonly int _rotation;     // applied to BOTH frames at display time so they match orientation

    private string? _sampleDir;
    private string? _samplePath;
    private CancellationTokenSource? _playCts;
    private CancellationTokenSource? _stepCts;

    internal CompareViewModel(
        string sourcePath, double durationSeconds, int fps, int sourceWidth, int sourceHeight,
        LibavExportOptions options, int rotation, double anchorSeconds = -1)
    {
        _sourcePath = sourcePath;
        _options = options;
        _fps = fps > 0 ? fps : 30;
        _rotation = ((rotation % 360) + 360) % 360;

        const double window = 15.0;
        double dur = durationSeconds > 0 ? durationSeconds : window;
        _windowLen = Math.Max(0.5, Math.Min(window, dur));
        _t0 = ComputeSampleStart(dur, _windowLen, anchorSeconds);

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
    public double WindowLength => _windowLen;

    // Sample-window start: anchored at the caller's playhead (so the compare reflects the frame being viewed),
    // or a representative segment ~10% in when no anchor is given (anchorSeconds &lt; 0). Clamped to fit the clip.
    internal static double ComputeSampleStart(double durationSeconds, double windowLen, double anchorSeconds)
    {
        double anchor = anchorSeconds >= 0 ? anchorSeconds : durationSeconds * 0.1;
        return Math.Clamp(anchor, 0, Math.Max(0, durationSeconds - windowLen));
    }

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

    public string TimeText => $"{Fmt(_positionSeconds)} / {Fmt(_windowLen)}";

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

    private bool _isPreparing = true;
    public bool IsPreparing { get => _isPreparing; private set => this.RaiseAndSetIfChanged(ref _isPreparing, value); }

    private string _statusText = string.Empty;
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ReactiveCommand<Unit, Unit> PlayPauseCommand { get; }
    public ReactiveCommand<Unit, Unit> StepBackCommand { get; }
    public ReactiveCommand<Unit, Unit> StepForwardCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipBackCommand { get; }
    public ReactiveCommand<Unit, Unit> SkipForwardCommand { get; }

    /// <summary>Encodes the sample, then shows the first frame of both. Call once after the window opens.</summary>
    public async Task InitializeAsync()
    {
        IsPreparing = true;
        try
        {
            _sampleDir = Path.Combine(Path.GetTempPath(), "GimmeCapture_Compare_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sampleDir);
            _samplePath = Path.Combine(_sampleDir, "sample.mp4");

            var ranges = new[] { new LibavClipExporter.SourceRange(_t0, _t0 + _windowLen) };
            bool ok = await Task.Run(() => LibavClipExporter.TryExport(
                _sourcePath, ranges, _samplePath, VideoQuality.Medium, options: _options));

            if (!ok || !File.Exists(_samplePath) || new FileInfo(_samplePath).Length == 0)
            {
                _samplePath = null;
                StatusText = LocalizationService.Instance["CompressQueueFailed"];
                return;
            }

            await ShowFrameAtAsync(0);
            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.CompareInit", ex);
            StatusText = LocalizationService.Instance["CompressQueueFailed"];
        }
        finally
        {
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
        if (_samplePath == null)
        {
            return Task.CompletedTask;
        }

        _stepCts?.Cancel();
        _playCts?.Cancel();
        _playCts = new CancellationTokenSource();
        CancellationToken ct = _playCts.Token;
        IsPlaying = true;

        double start = PositionSeconds >= _windowLen - 0.05 ? 0 : PositionSeconds; // replay from start if at end
        PositionSeconds = start;

        // Original (left): plays the source window [t0+start, t1]; stops at the window end.
        _ = Task.Run(async () =>
        {
            try
            {
                using var p = new LibavVideoFramePlayer();
                await p.PlayAsync(_sourcePath, _decodeW, _decodeH, _t0 + start, 1.0, false, (data, ts) =>
                {
                    double pos = ts - _t0;
                    if (pos > _windowLen)
                    {
                        throw new OperationCanceledException(); // reached the window end
                    }
                    Bitmap? bmp = BytesToBitmap(data);
                    Dispatcher.UIThread.Post(() => { SourceFrame = bmp; PositionSeconds = pos; });
                }, ct);
            }
            catch (OperationCanceledException) { /* paused or window end */ }
            catch (Exception ex) { AppLog.Error("Compress.ComparePlaySource", ex); }
            finally { Dispatcher.UIThread.Post(() => IsPlaying = false); }
        }, ct);

        // Compressed (right): plays the whole sample [0, windowLen].
        _ = Task.Run(async () =>
        {
            try
            {
                using var p = new LibavVideoFramePlayer();
                await p.PlayAsync(_samplePath!, _decodeW, _decodeH, start, 1.0, false, (data, _) =>
                {
                    Bitmap? bmp = BytesToBitmap(data);
                    Dispatcher.UIThread.Post(() => SampleFrame = bmp);
                }, ct);
            }
            catch (OperationCanceledException) { /* paused */ }
            catch (Exception ex) { AppLog.Error("Compress.ComparePlaySample", ex); }
        }, ct);

        return Task.CompletedTask;
    }

    private void Pause()
    {
        _playCts?.Cancel();
        IsPlaying = false;
    }

    // Slider scrubbing (driven by the window code-behind): pause on grab, seek to the dropped position on release.
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

    // Decodes both videos at the shared relative position and shows them (paused frame-step / seek).
    private async Task ShowFrameAtAsync(double pos)
    {
        if (_samplePath == null)
        {
            return;
        }

        pos = Math.Clamp(pos, 0, _windowLen);
        PositionSeconds = pos;

        _stepCts?.Cancel();
        _stepCts = new CancellationTokenSource();
        CancellationToken ct = _stepCts.Token;

        byte[]? src = await LibavVideoFramePlayer.DecodeFrameAtAsync(_sourcePath, _t0 + pos, _decodeW, _decodeH, ct);
        byte[]? smp = await LibavVideoFramePlayer.DecodeFrameAtAsync(_samplePath, pos, _decodeW, _decodeH, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (src != null) { SourceFrame = BytesToBitmap(src); }
        if (smp != null) { SampleFrame = BytesToBitmap(smp); }
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
        _playCts?.Dispose();
        _stepCts?.Dispose();
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
