using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReactiveUI;
using System;
using System.Buffers;
using System.Reactive;
using System.Threading.Tasks;
using GimmeCapture.Models;
using System.Reactive.Linq;
using GimmeCapture.ViewModels.Shared;
using System.Threading;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;
using GimmeCapture.Services.Core.Infrastructure;
using NAudio.Wave;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel : FloatingWindowViewModelBase, IDrawingToolViewModel, IAsyncDisposable
{
    private readonly int _uiFrameUpdateIntervalMs;
    private readonly int _uiTimeUpdateIntervalMs;
    private readonly SemaphoreSlim _playSemaphore = new(1, 1);
    private readonly object _disposeLock = new();
    private int _playbackGeneration = 0;
    internal volatile bool _trimEndReached;
    private bool _isDisposed;
    private Task? _disposeTask;
    private Task? _bootMediaTask;
    private readonly object _latestFrameLock = new();
    private readonly object _videoBitmapLock = new();
    private byte[]? _latestFrameData;
    private int _latestFrameLength;
    private int _latestFrameGeneration;
    private long _lastFrameUiPostTimestampMs;
    private long _lastTimeUiPostTimestampMs;
    private int _isFrameUiPostPending;
    private int _isTimeUiPostPending;
    // Media / Video Properties & State
    private WriteableBitmap? _videoBitmap;
    public WriteableBitmap? VideoBitmap
    {
        get => _videoBitmap;
        set => this.RaiseAndSetIfChanged(ref _videoBitmap, value);
    }

    public string VideoPath { get; }
    private readonly string _ffmpegPath;
    public string FFmpegPath => _ffmpegPath;
    public VideoCodec VideoCodec => _appSettingsService?.Settings.VideoCodec ?? VideoCodec.H264;
    private CancellationTokenSource? _playCts;
    private Task? _playbackTask;
    private readonly int _width;
    private readonly int _height;
    private IWavePlayer? _pinAudioPlayer;
    private WaveStream? _audioPlaybackStream;
    private CancellationTokenSource? _pinAudioDecodeCts;
    private bool _isMuted;
    private readonly LibavVideoFramePlayer _nativeFramePlayer = new();

    private bool _isExporting;
    public bool IsExporting
    {
        get => _isExporting;
        set => this.RaiseAndSetIfChanged(ref _isExporting, value);
    }

    private double _exportProgress;
    public double ExportProgress
    {
        get => _exportProgress;
        set => this.RaiseAndSetIfChanged(ref _exportProgress, value);
    }

    public bool IsPlaying => _isPlaybackActive;
    private bool _isPlaybackActive = true;

    private bool _isLooping = true;
    public bool IsLooping
    {
        get => _isLooping;
        set => this.RaiseAndSetIfChanged(ref _isLooping, value);
    }

    private TimeSpan _totalDuration = TimeSpan.Zero;
    public TimeSpan TotalDuration
    {
        get => _totalDuration;
        set
        {
            this.RaiseAndSetIfChanged(ref _totalDuration, value);
            this.RaisePropertyChanged(nameof(FormattedTime));
            // Duration is probed on a background task; if the timeline was seeded before it was
            // known, repair the degenerate full-clip segment now (on the UI thread).
            Avalonia.Threading.Dispatcher.UIThread.Post(RepairFullClipSegmentForDuration);
        }
    }

    private TimeSpan _currentTime = TimeSpan.Zero;
    public TimeSpan CurrentTime
    {
        get => _currentTime;
        set 
        {
            this.RaiseAndSetIfChanged(ref _currentTime, value);
            this.RaisePropertyChanged(nameof(FormattedTime));
        }
    }

    public string FormattedTime => $"{_currentTime:mm\\:ss} / {_totalDuration:mm\\:ss}";

    private double _seekTargetSeconds = -1;
    private double _playbackSpeed = 1.0;

    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set 
        {
            if (Math.Abs(_playbackSpeed - value) < 0.01) return;
            
            _playbackSpeed = value;
            this.RaisePropertyChanged(nameof(PlaybackSpeed));
            this.RaisePropertyChanged(nameof(PlaybackSpeedText));
            
            // Speed change entails seeking from current point to avoid restart
            if (_isPlaybackActive)
            {
                _seekTargetSeconds = _currentTime.TotalSeconds;
                StartPlayback();
            }
        }
    }

    public string PlaybackSpeedText => $"{_playbackSpeed:F1}x";
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            this.RaiseAndSetIfChanged(ref _isMuted, value);
            this.RaisePropertyChanged(nameof(MuteTooltip));
            UpdateAudioStateFromPlayback();
        }
    }

    private bool _isDraggingSlider;
    private CancellationTokenSource? _seekDebounceCts;

    public double CurrentTimeSeconds
    {
        get => _currentTime.TotalSeconds;
        set
        {
            if (Math.Abs(_currentTime.TotalSeconds - value) > 0.01)
            {
                // Mark as user-dragging to suppress playback loop updates
                _isDraggingSlider = true;
                _currentTime = TimeSpan.FromSeconds(value);
                this.RaisePropertyChanged(nameof(FormattedTime));
                
                // Auto-pause immediately upon scrubbing
                if (_isPlaybackActive)
                {
                    _isPlaybackActive = false;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        this.RaisePropertyChanged(nameof(IsPlaying)));
                }

                // Debounce the actual seek — only fire after user stops dragging for 300ms
                _seekDebounceCts?.Cancel();
                _seekDebounceCts = new CancellationTokenSource();
                var token = _seekDebounceCts.Token;
                DebounceSeekAsync(value, token).Forget("FloatingVideo.DebounceSeek");
            }
        }
    }

    public ReactiveCommand<Unit, Unit> TogglePlaybackCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> FastForwardCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RewindCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleLoopCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CycleSpeedCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleMuteCommand { get; private set; } = null!;

    public System.Action? RequestRedraw { get; set; }

    // Overrides
    public override FloatingTool CurrentTool
    {
        get => base.CurrentTool;
        set 
        {
            if (base.CurrentTool == value) return;

            if (value == FloatingTool.Selection)
            {
                PausePlaybackForPreciseInteraction();
            }
            
            if (value != FloatingTool.None)
            {
                IsTrimmingMode = false;
                IsTimelineMode = false;
                CurrentAnnotationTool = AnnotationType.None;
            }

            base.CurrentTool = value;
            this.RaisePropertyChanged(nameof(IsSelectionMode));
            this.RaisePropertyChanged(nameof(IsAnyToolActive));
        }
    }

    public override AnnotationType CurrentAnnotationTool
    {
        get => base.CurrentAnnotationTool;
        set 
        {
            if (base.CurrentAnnotationTool == value) return;
            
            if (value != AnnotationType.None)
            {
                IsTrimmingMode = false;
                IsTimelineMode = false;
                CurrentTool = FloatingTool.None;
            }

            base.CurrentAnnotationTool = value;
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsTextToolActive));
            this.RaisePropertyChanged(nameof(IsPenToolActive));
            this.RaisePropertyChanged(nameof(IsAnyToolActive));
        }
    }
    public bool ShowIconSettings => false;

    // Esc exits trim mode before the generic tool/annotation cancel.
    protected override bool TryCancelModeSpecific()
    {
        if (IsTrimmingMode)
        {
            IsTrimmingMode = false;
            return true;
        }

        if (IsTimelineMode)
        {
            IsTimelineMode = false;
            return true;
        }

        return false;
    }

    // Hotkey Proxies
    public override string PinHotkey => _appSettingsService?.Settings.Snip.Pin ?? base.PinHotkey;
    public override string UndoHotkey => _appSettingsService?.Settings.Record.Undo ?? base.UndoHotkey;
    public override string RedoHotkey => _appSettingsService?.Settings.Record.Redo ?? base.RedoHotkey;
    public override string ClearHotkey => _appSettingsService?.Settings.Record.Clear ?? base.ClearHotkey;
    public override string SaveHotkey => _appSettingsService?.Settings.Record.Save ?? base.SaveHotkey;
    public override string CopyHotkey => _appSettingsService?.Settings.Record.Copy ?? base.CopyHotkey;
    // Close is a fixed window-local shortcut (Ctrl+W); the pin is not closed by the record Close setting.
    public override string CloseHotkey => base.CloseHotkey;
    public string PlaybackHotkey => _appSettingsService?.Settings.Record.Playback ?? "Space"; // Specific to Video
    public string MuteHotkey => "Shift+M";
    
    public override string RectangleHotkey => _appSettingsService?.Settings.Record.Rectangle ?? base.RectangleHotkey;
    public override string EllipseHotkey => _appSettingsService?.Settings.Record.Ellipse ?? base.EllipseHotkey;
    public override string ArrowHotkey => _appSettingsService?.Settings.Record.Arrow ?? base.ArrowHotkey;
    public override string LineHotkey => _appSettingsService?.Settings.Record.Line ?? base.LineHotkey;
    public override string PenHotkey => _appSettingsService?.Settings.Record.Pen ?? base.PenHotkey;
    public override string TextHotkey => _appSettingsService?.Settings.Record.Text ?? base.TextHotkey;
    public override string MosaicHotkey => _appSettingsService?.Settings.Record.Mosaic ?? base.MosaicHotkey;
    public override string BlurHotkey => _appSettingsService?.Settings.Record.Blur ?? base.BlurHotkey;
    public override string SelectionHotkey => _appSettingsService?.Settings.Snip.SelectionMode ?? base.SelectionHotkey;
    public override string CropHotkey => _appSettingsService?.Settings.Snip.CropMode ?? base.CropHotkey;

    // Tooltip Hints
    public string UndoTooltip => $"{LocalizationService.Instance["Undo"]} ({UndoHotkey})";
    public string RedoTooltip => $"{LocalizationService.Instance["Redo"]} ({RedoHotkey})";
    public string ClearTooltip => $"{LocalizationService.Instance["Clear"]} ({ClearHotkey})";
    public string SaveTooltip => $"{LocalizationService.Instance["TipSave"]} ({SaveHotkey})";
    public string CopyTooltip => $"{LocalizationService.Instance["TipCopy"]} ({CopyHotkey})";
    public string PinTooltip => $"{LocalizationService.Instance["TipPin"]} ({PinHotkey})";
    public string RectangleTooltip => $"{LocalizationService.Instance["TipRectangle"]} ({RectangleHotkey})";
    public string EllipseTooltip => $"{LocalizationService.Instance["TipEllipse"]} ({EllipseHotkey})";
    public string ArrowTooltip => $"{LocalizationService.Instance["TipArrow"]} ({ArrowHotkey})";
    public string LineTooltip => $"{LocalizationService.Instance["TipLine"]} ({LineHotkey})";
    public string PenTooltip => $"{LocalizationService.Instance["TipPen"]} ({PenHotkey})";
    public string TextTooltip => $"{LocalizationService.Instance["TipText"]} ({TextHotkey})";
    public string CalloutTooltip => LocalizationService.Instance["TipCallout"];
    public string MosaicTooltip => $"{LocalizationService.Instance["TipMosaic"]} ({MosaicHotkey})";
    public string BlurTooltip => $"{LocalizationService.Instance["TipBlur"]} ({BlurHotkey})";
    public string HighlighterTooltip => LocalizationService.Instance["TipHighlighter"];
    public string StepTooltip => LocalizationService.Instance["TipStep"];
    public string BringToFrontTooltip => LocalizationService.Instance["TipBringToFront"];
    public string SendToBackTooltip => LocalizationService.Instance["TipSendToBack"];
    public string PlaybackTooltip => $"{LocalizationService.Instance["ActionPlayback"]} ({PlaybackHotkey})";
    public string MuteTooltip => IsMuted
        ? $"{(LocalizationService.Instance["RecordUnmute"] ?? "Unmute")} ({MuteHotkey})"
        : $"{(LocalizationService.Instance["RecordMute"] ?? "Mute")} ({MuteHotkey})";
    public string ToggleToolbarTooltip => $"{LocalizationService.Instance["ActionToolbar"]} ({_appSettingsService?.Settings.Record.Toolbar ?? "H"})";
    public string CloseTooltip => $"{LocalizationService.Instance["ActionClose"]} ({CloseHotkey})";
    public string RepeatTooltip => $"{LocalizationService.Instance["ActionRepeat"]}";
    public string SelectionTooltip => $"{LocalizationService.Instance["TipSelectionArea"]} ({SelectionHotkey})";
    public string CropTooltip => $"{LocalizationService.Instance["TipCrop"]} ({CropHotkey})";
    public string PinSelectionTooltip => $"{LocalizationService.Instance["TipPinSelection"]} ({PinHotkey})";

    public override Avalonia.Thickness WindowPadding
    {
        get
        {
            // If decorations are hidden, we just need the standard margin (e.g. 10 for shadow/resize handles).
            // If they are visible, we need enough space for the wings (WingWidth) + border + buffer
            double hPad = HidePinDecoration ? 10 : (WingWidth + SelectionThickness + 10);
            double vPad = 28; // Increased from 25
            
            // RESERVE space for floating toolbar if visible
            // Two rows: Toolbar Height(32*2) + Spacing(4) + Bottom Margin(10) = 78px
            double bottomPad = vPad;
            if (ShowToolbar) bottomPad += 78;
            
            // 裁切面板額外空間：拉桿(28) + 時間輸入(28) + spacing + padding ≈ 75px
            if (IsTrimmingMode) bottomPad += 75;

            // 時間軸段落列：分隔線 + 段落 chip 列(24) + spacing ≈ 40px
            if (IsTimelineMode) bottomPad += 40;

            return new Avalonia.Thickness(hPad, vPad, hPad, bottomPad);
        }
    }


    // Dependencies
    private readonly GimmeCapture.Services.Abstractions.IClipboardService _clipboardService;
    public GimmeCapture.Services.Abstractions.IClipboardService ClipboardService => _clipboardService;
    private readonly AppSettingsService? _appSettingsService;
    public AppSettingsService? AppSettingsService => _appSettingsService;

    // Actions & Commands
    public System.Func<Task>? CopyAction { get; set; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CropCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> PinSelectionCommand { get; private set; } = null!;
    public System.Func<Task<string?>>? PickSaveFileAction { get; set; }

    // Spawns a new pinned video window (cropped selection). Signature mirrors
    // SnipWindowViewModel.OpenPinnedVideoWindowAction; self-wired by the view so
    // any floating video window can pin a sibling.
    // (path, pixelWidth, pixelHeight, originalWidth, originalHeight, borderColor, borderThickness, hideDecoration, hideBorder)
    public System.Action<string, int, int, double, double, Avalonia.Media.Color, double, bool, bool>? OpenPinnedVideoWindowAction { get; set; }

    // Annotation Proxies
    public bool CanUndo => HasUndo;
    public bool CanRedo => HasRedo;

    private static int NormalizeVideoDimension(int value)
    {
        var positive = Math.Max(1, value);
        var even = (positive / 2) * 2;
        return Math.Max(2, even);
    }

    private static int FpsToIntervalMs(int fps, int defaultFps)
    {
        int safeFps = Math.Clamp(fps > 0 ? fps : defaultFps, 1, 120);
        return Math.Max(1, 1000 / safeFps);
    }

    public FloatingVideoViewModel(string videoPath, string ffmpegPath, int width, int height, double originalWidth, double originalHeight, Avalonia.Media.Color borderColor, double borderThickness, bool hideDecoration, bool hideBorder, GimmeCapture.Services.Abstractions.IClipboardService clipboardService, AppSettingsService? appSettingsService)
    {
        VideoPath = videoPath;
        _ffmpegPath = ffmpegPath;
        _width = NormalizeVideoDimension(width); // Ensure even and valid for bitmap/FFmpeg
        _height = NormalizeVideoDimension(height);
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
        DisplayWidth = originalWidth;
        DisplayHeight = originalHeight;
        BorderColor = borderColor;
        BorderThickness = borderThickness;
        HidePinDecoration = hideDecoration;
        HidePinBorder = hideBorder;
        CurrentThickness = 4.0;
        _clipboardService = clipboardService;
        _appSettingsService = appSettingsService;
        _uiFrameUpdateIntervalMs = FpsToIntervalMs(_appSettingsService?.Settings.PlaybackUiFps ?? 30, 30);
        _uiTimeUpdateIntervalMs = FpsToIntervalMs(_appSettingsService?.Settings.PlaybackTimelineFps ?? 15, 15);

        // Apply Default Toolbar Visibility
        ShowToolbar = !(_appSettingsService?.Settings.DefaultHideRecordToolbar ?? false);

        InitializeBaseCommands();
        InitializeActionCommands();
        // InitializeToolbarCommands(); // Handled by Base
        InitializeAnnotationCommands();
        InitializeTrimCommands();
        InitializeSegmentCommands();
        InitializeMediaCommands(); // Media init last as it starts playback
    }

    public Task BeginDispose()
    {
        lock (_disposeLock)
        {
            if (_disposeTask == null)
            {
                _isDisposed = true;
                Interlocked.Increment(ref _playbackGeneration);
                RequestRedraw = null;
                OpenPinnedVideoWindowAction = null;

                var playbackCts = CancelPlaybackToken();
                var audioDecodeCts = CancelAudioDecodeToken();
                _disposeTask = Task.Run(() => DisposeCoreAsync(playbackCts, audioDecodeCts));
            }

            return _disposeTask;
        }
    }

    public ValueTask DisposeAsync() => new(BeginDispose());

    public override void Dispose()
    {
        BeginDispose().Forget("FloatingVideo.Dispose");
    }

    private async Task DisposeCoreAsync(CancellationTokenSource? playbackCts, CancellationTokenSource? audioDecodeCts)
    {
        playbackCts?.Dispose();
        audioDecodeCts?.Dispose();

        _seekDebounceCts?.Cancel();
        _seekDebounceCts?.Dispose();
        _seekDebounceCts = null;
        StopAudioPlayback();

        var playbackCompleted = true;
        try
        {
            if (_bootMediaTask is not null)
            {
                await WaitForTaskOrTimeoutAsync(
                    _bootMediaTask,
                    TimeSpan.FromSeconds(2),
                    "FloatingVideo.BootMedia");
            }

            if (_playbackTask is not null)
            {
                playbackCompleted = await WaitForTaskOrTimeoutAsync(
                    _playbackTask,
                    TimeSpan.FromSeconds(2),
                    "FloatingVideo.Playback");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Video disposal wait failed: {ex}");
        }

        StopAudioPlayback();
        _nativeFramePlayer.Dispose();
        if (playbackCompleted)
        {
            _playSemaphore.Dispose();
        }
        
        base.Dispose();

        WriteableBitmap? oldBitmap;
        lock (_videoBitmapLock)
        {
            oldBitmap = _videoBitmap;
            _videoBitmap = null;
        }
        oldBitmap?.Dispose();
        lock (_latestFrameLock)
        {
            if (_latestFrameData != null)
            {
                ArrayPool<byte>.Shared.Return(_latestFrameData);
            }
            _latestFrameData = null;
            _latestFrameLength = 0;
        }
    }

    private static async Task<bool> WaitForTaskOrTimeoutAsync(Task task, TimeSpan timeout, string operation)
    {
        try
        {
            var timeoutTask = Task.Delay(timeout);
            if (await Task.WhenAny(task, timeoutTask).ConfigureAwait(false) != task)
            {
                System.Diagnostics.Debug.WriteLine($"{operation} did not finish within {timeout.TotalMilliseconds:0} ms; continuing disposal.");
                return false;
            }

            await task.ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"{operation} finished with error during disposal: {ex}");
            return true;
        }
    }

    private async Task DebounceSeekAsync(double value, CancellationToken cancellationToken)
    {
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        _isDraggingSlider = false;
        _seekTargetSeconds = value;
        StartPlayback();
    }

    private void PausePlaybackForPreciseInteraction()
    {
        if (!_isPlaybackActive)
        {
            return;
        }

        _isPlaybackActive = false;
        CancelPlaybackInBackground();
        this.RaisePropertyChanged(nameof(IsPlaying));
    }
}
