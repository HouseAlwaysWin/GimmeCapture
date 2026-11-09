using System;
using System.IO;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.ViewModels.Shared;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace GimmeCapture.ViewModels.Main;

/// <summary>
/// Drives the inline side-by-side quality compare hosted in the editor preview. The scrubber spans the WHOLE
/// clip; the original (left) is decoded straight from the source at any position (instant), while the compressed
/// (right) is produced by encoding a short window that FOLLOWS the playhead on demand — so any point can be
/// compared quickly without encoding the whole (potentially 4K) file. Playback starts on the window already encoded
/// and encodes the next while it plays, so it carries through the clip. Both frames are rotated equally to match the
/// editor's orientation.
/// </summary>
internal sealed class CompareViewModel : ViewModelBase, IDisposable
{
    private readonly int _fps;
    private readonly int _decodeW;
    private readonly int _decodeH;
    private readonly double _duration;      // full source duration — the slider spans this
    private readonly int _rotation;         // applied to BOTH frames at display time so they match orientation
    private readonly double _anchorSeconds;  // initial window position (editor playhead); < 0 = ~10% in

    // What the compressed side shows, encoded a stretch at a time around the playhead.
    private readonly CompareSampleWindows _windows;
    private CancellationTokenSource? _playCts;
    private CancellationTokenSource? _stepCts;
    private volatile bool _disposed;

    // How many waits for a window are under way, each showing the overlay on the compressed side. UI thread only.
    private int _windowWaits;

    // Bumped on each play and pause (UI thread), so a stopped play loop's frames, playhead and "stopped" don't land
    // after a seek or a new play.
    private int _playGeneration;

    // Playback hands each side's newest decoded frame to the UI thread here, which draws it into that side's one
    // bitmap (see VideoFrameBitmap).
    private readonly LatestFrameSlot _sourceFrames = new();
    private readonly LatestFrameSlot _sampleFrames = new();

    // Each side's frames, paused or playing, from a decoder that keeps its file open: for the source the editor's own,
    // which the editor owns, and for the sample one that reads the window under the playhead.
    private readonly LibavStillFrameDecoder _sourceDecoder;
    private LibavStillFrameDecoder? _sampleDecoder;
    private LibavStillFrameDecoder? _nextSampleDecoder; // for the window encoded ahead, open before the playback gets there
    private readonly object _sampleDecoderLock = new();

    /// <param name="source">The decoder of the video being compared, which stays its owner's.</param>
    /// <param name="durationSeconds">The video's length.</param>
    /// <param name="fps">The video's frame rate, for a step.</param>
    /// <param name="options">The settings being compared.</param>
    /// <param name="rotation">How the editor shows the video turned.</param>
    /// <param name="anchorSeconds">Where to open, or below zero a tenth of the way in.</param>
    /// <param name="encodeWindow">Encodes a stretch of the video, unless the settings are to.</param>
    internal CompareViewModel(
        LibavStillFrameDecoder source, double durationSeconds, int fps,
        LibavExportOptions options, int rotation, double anchorSeconds = -1,
        CompareSampleWindows.EncodeWindow? encodeWindow = null)
    {
        _sourceDecoder = source;
        _fps = fps > 0 ? fps : 30;
        _rotation = ((rotation % 360) + 360) % 360;

        _duration = durationSeconds > 0 ? durationSeconds : CompareSampleWindows.ShortSeconds;
        _anchorSeconds = anchorSeconds;
        _decodeW = source.Width;
        _decodeH = source.Height;
        _windows = new CompareSampleWindows(_duration, encodeWindow ?? ((start, length, path, ct) => LibavClipExporter.TryExport(
            source.VideoPath, [new LibavClipExporter.SourceRange(start, start + length)], path, VideoQuality.Medium,
            cancellationToken: ct, options: options)));

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

        Title = Path.GetFileName(source.VideoPath);
        StatusText = Localization["CompressComparePreparing"];
    }

    public string Title { get; }

    /// <summary>The scrubber's max — the whole source duration (the compare now spans the full clip).</summary>
    public double WindowLength => _duration;

    // Each side's frame bitmap: made by the first frame, then every later frame of the same size is drawn into it.
    private WriteableBitmap? _sourceFrame;
    public Bitmap? SourceFrame => _sourceFrame;

    private WriteableBitmap? _sampleFrame;
    public Bitmap? SampleFrame => _sampleFrame;

    /// <summary>Set by the view: repaints both frames after one was drawn into the bitmap it already shows.</summary>
    public Action? RequestRedraw { get; set; }

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
            _windows.Open();

            double startPos = _anchorSeconds >= 0 ? Math.Clamp(_anchorSeconds, 0, _duration) : _duration * 0.1;
            await ShowFrameAtAsync(startPos);
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.CompareInit", ex);
            StatusText = Localization["CompressQueueFailed"];
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
        int gen = ++_playGeneration;
        IsPlaying = true;

        double start = PositionSeconds >= _duration - 0.05 ? 0 : PositionSeconds; // replay from start if at end
        PositionSeconds = start;

        _ = Task.Run(() => PlayLoopAsync(start, gen, ct), ct);
        return Task.CompletedTask;
    }

    // Plays source + sample in sync: what is encoded plays at once, and the window after it is encoded while it plays.
    // Where the encoder is slower than the picture, the playback waits for it at the window's end.
    private async Task PlayLoopAsync(double startPos, int gen, CancellationToken ct)
    {
        try
        {
            double pos = startPos;
            while (!ct.IsCancellationRequested && pos < _duration - 0.05)
            {
                CompareSampleWindow? win = _windows.PlayableAt(pos) ?? await ShowingWaitAsync(_windows.ToPlayFromAsync(pos));
                if (win == null || ct.IsCancellationRequested)
                {
                    break;
                }

                double winStart = win.Start;
                double winEnd = Math.Min(_duration, win.End);
                double playFrom = Math.Max(pos, winStart);
                if (playFrom >= winEnd - 0.02)
                {
                    break; // nothing left to play in this (final) window
                }

                OpenWhenEncodedAsync(_windows.EncodeAhead(winEnd, winEnd - playFrom)).Forget("Compress.CompareOpenNextWindow");
                LibavStillFrameDecoder sampleDecoder = SampleDecoderFor(win);

                using var winCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                CancellationToken wct = winCts.Token;

                // Align the two streams at the window start: the 4K source takes longer to give a frame than the small
                // sample clip, which would otherwise race ahead. Hold the sample until the source shows its first frame
                // of this window.
                var srcReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

                var sampleTask = Task.Run(async () =>
                {
                    try
                    {
                        await srcReady.Task.WaitAsync(wct).ConfigureAwait(false);
                        await sampleDecoder.PlayPiecesAsync(OnePiece(playFrom - winStart, win.Length), (data, _) =>
                        {
                            if (_sampleFrames.Offer(data, _decodeW, _decodeH, gen, 0))
                            {
                                Dispatcher.UIThread.Post(DrawPlayedSample);
                            }
                        }, wct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { AppLog.Error("Compress.ComparePlaySample", ex); }
                }, wct);

                try
                {
                    // Original (left): play [playFrom, winEnd]; stop at the window end.
                    await _sourceDecoder.PlayPiecesAsync(OnePiece(playFrom, winEnd), (data, ts) =>
                    {
                        srcReady.TrySetResult(); // first frame of the window releases the sample to start in step
                        // Only the newest frame of each side waits for the UI thread; a later one replaces it.
                        if (_sourceFrames.Offer(data, _decodeW, _decodeH, gen, ts))
                        {
                            Dispatcher.UIThread.Post(DrawPlayedSource);
                        }
                    }, wct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { /* paused */ }
                finally
                {
                    // Always release the barrier + stop and await the sample player, whatever ended the source
                    // (window end, pause, or a decode fault), so it is never stranded and winCts isn't disposed
                    // while still in use.
                    srcReady.TrySetResult();
                    winCts.Cancel();
                    try { await sampleTask; } catch { /* already stopping */ }
                }

                if (ct.IsCancellationRequested)
                {
                    break;                       // paused by the user
                }

                pos = winEnd;                    // advance to the next contiguous window
                double next = pos;
                Dispatcher.UIThread.Post(() =>
                {
                    if (gen == _playGeneration)
                    {
                        PositionSeconds = next;
                    }
                });
            }
        }
        catch (OperationCanceledException) { /* paused */ }
        catch (Exception ex) { AppLog.Error("Compress.ComparePlay", ex); }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (gen == _playGeneration)
                {
                    IsPlaying = false; // a newer play owns the button; a pause has already reset it
                }
            });
        }
    }

    private static PlaybackPieces OnePiece(double start, double end) =>
        new([new PlaybackPiece(start, end, 1.0)], 0, start, () => false);

    private void Pause()
    {
        _playCts?.Cancel();
        _playGeneration++;
        IsPlaying = false;
    }

    // Shows the overlay on the compressed side for as long as the picture waits for this window, which an encode going on
    // behind a playback does not make it do.
    private async Task<CompareSampleWindow?> ShowingWaitAsync(Task<CompareSampleWindow?> window)
    {
        if (window.IsCompleted)
        {
            return await window;
        }

        Dispatcher.UIThread.Post(() =>
        {
            _windowWaits++;
            StatusText = Localization["CompressComparePreparing"];
            IsPreparing = true;
        });

        try
        {
            return await window.ConfigureAwait(false);
        }
        finally
        {
            Dispatcher.UIThread.Post(() => IsPreparing = --_windowWaits > 0);
        }
    }

    // UI thread: draws the newest frame each side's playback left, unless a pause / seek / new play superseded it.
    private void DrawPlayedSource()
    {
        if (_sourceFrames.Take() is not { } frame)
        {
            return;
        }

        try
        {
            if (!_disposed && frame.Ticket == _playGeneration)
            {
                DrawFrame(ref _sourceFrame, nameof(SourceFrame), frame.Pixels);
                PositionSeconds = frame.Seconds;
            }
        }
        finally
        {
            _sourceFrames.Return(frame.Pixels);
        }
    }

    private void DrawPlayedSample()
    {
        if (_sampleFrames.Take() is not { } frame)
        {
            return;
        }

        try
        {
            if (!_disposed && frame.Ticket == _playGeneration)
            {
                DrawFrame(ref _sampleFrame, nameof(SampleFrame), frame.Pixels);
            }
        }
        finally
        {
            _sampleFrames.Return(frame.Pixels);
        }
    }

    // UI thread. Draws a decoded frame into one side's bitmap, rotated like the editor shows it (both sides alike);
    // the first frame makes the bitmap, which later frames reuse.
    private void DrawFrame(ref WriteableBitmap? shown, string propertyName, ReadOnlySpan<byte> bgra)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            WriteableBitmap? drawn = VideoFrameBitmap.Draw(
                shown, bgra, _decodeW, _decodeH, new PixelRect(0, 0, _decodeW, _decodeH), _rotation);
            if (drawn == null)
            {
                return;
            }

            if (ReferenceEquals(drawn, shown))
            {
                RequestRedraw?.Invoke();
                return;
            }

            WriteableBitmap? replaced = shown;
            shown = drawn;
            this.RaisePropertyChanged(propertyName);
            replaced?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Error("Compress.CompareFrameBuild", ex);
        }
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
            byte[]? src = await _sourceDecoder.DecodeFrameAtAsync(pos, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
            if (src != null) { DrawFrame(ref _sourceFrame, nameof(SourceFrame), src); }

            CompareSampleWindow? win = await ShowingWaitAsync(_windows.AroundAsync(pos));
            if (ct.IsCancellationRequested || win == null)
            {
                return;
            }

            byte[]? smp = await SampleDecoderFor(win).DecodeFrameAtAsync(pos - win.Start, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
            if (smp != null) { DrawFrame(ref _sampleFrame, nameof(SampleFrame), smp); }
        }
        catch (OperationCanceledException) { /* superseded by a newer seek/step */ }
        catch (Exception ex) { AppLog.Error("Compress.CompareShowFrame", ex); }
    }

    // The sample's decoder for this window, made when the window changes. A decoder stays with one window: a playback
    // that reaches the next one, on the pool, and a paused frame, on the UI thread, both come here.
    private LibavStillFrameDecoder SampleDecoderFor(CompareSampleWindow win)
    {
        lock (_sampleDecoderLock)
        {
            if (_sampleDecoder is { } current && current.VideoPath == win.Path)
            {
                return current;
            }

            _sampleDecoder?.Dispose();
            if (_nextSampleDecoder?.VideoPath == win.Path)
            {
                _sampleDecoder = _nextSampleDecoder;
                _nextSampleDecoder = null;
            }
            else
            {
                _sampleDecoder = new LibavStillFrameDecoder(win.Path, _decodeW, _decodeH);
            }

            if (_disposed)
            {
                _sampleDecoder.Dispose(); // it gives no frames, and nothing later would let it go
            }

            return _sampleDecoder;
        }
    }

    // A window's decoder takes about 170 ms to open and give its first frame, for which the compressed side would stand
    // still at the start of every window a playback reaches: it opens now, while the window before it plays.
    private async Task OpenWhenEncodedAsync(Task<CompareSampleWindow?> ahead)
    {
        if (await ahead.ConfigureAwait(false) is not { } window)
        {
            return;
        }

        LibavStillFrameDecoder decoder;
        lock (_sampleDecoderLock)
        {
            if (_disposed || _sampleDecoder?.VideoPath == window.Path || _nextSampleDecoder?.VideoPath == window.Path)
            {
                return;
            }

            _nextSampleDecoder?.Dispose();
            decoder = new LibavStillFrameDecoder(window.Path, _decodeW, _decodeH);
            _nextSampleDecoder = decoder;
        }

        await decoder.DecodeFrameAtAsync(0).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _disposed = true;
        _playCts?.Cancel();
        _stepCts?.Cancel();
        lock (_sampleDecoderLock)
        {
            _sampleDecoder?.Dispose();
            _nextSampleDecoder?.Dispose();
        }

        _windows.Dispose();
        // Release the two decoded comparison frames (each an MB-sized Bitmap) — the window is closing.
        _sourceFrame?.Dispose();
        _sampleFrame?.Dispose();
        _sourceFrame = null;
        _sampleFrame = null;
        // The CancellationTokenSources are intentionally left for GC (none hold a wait handle): disposing them here would
        // race an in-flight play task into ObjectDisposedException on close.

        // Compare window closed — reclaim the two decoded frames just released.
        ProcessMemoryTrimService.RequestIdleTrimAsync("compare-closed").Forget("MemoryTrim.CompareClosed");
    }
}
