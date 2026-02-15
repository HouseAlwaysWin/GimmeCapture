using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;
using GimmeCapture.Models;
using System.Linq;
using System.Reactive.Linq;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.ViewModels.Shared;
using System.Threading;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel : FloatingWindowViewModelBase, IDrawingToolViewModel
{
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
                
                // Debounce the actual seek — only fire after user stops dragging for 300ms
                _seekDebounceCts?.Cancel();
                _seekDebounceCts = new CancellationTokenSource();
                var token = _seekDebounceCts.Token;
                _ = Task.Delay(300, token).ContinueWith(t =>
                {
                    if (!t.IsCanceled)
                    {
                        _isDraggingSlider = false;
                        _seekTargetSeconds = value;
                        if (!_isPlaybackActive)
                        {
                            _isPlaybackActive = true;
                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                                this.RaisePropertyChanged(nameof(IsPlaying)));
                        }
                        StartPlayback();
                    }
                }, TaskScheduler.Default);
            }
        }
    }

    public ReactiveCommand<Unit, Unit> TogglePlaybackCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> FastForwardCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RewindCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleLoopCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CycleSpeedCommand { get; private set; } = null!;

    public System.Action? RequestRedraw { get; set; }

    // Overrides
    public override FloatingTool CurrentTool
    {
        get => base.CurrentTool;
        set 
        {
            if (base.CurrentTool == value) return;
            
            if (value != FloatingTool.None)
            {
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
    
    // Hotkey Proxies
    public override string CopyHotkey => _appSettingsService?.Settings.CopyHotkey ?? base.CopyHotkey;
    public override string PinHotkey => _appSettingsService?.Settings.PinHotkey ?? base.PinHotkey;
    public override string UndoHotkey => _appSettingsService?.Settings.UndoHotkey ?? base.UndoHotkey;
    public override string RedoHotkey => _appSettingsService?.Settings.RedoHotkey ?? base.RedoHotkey;
    public override string ClearHotkey => _appSettingsService?.Settings.ClearHotkey ?? base.ClearHotkey;
    public override string SaveHotkey => _appSettingsService?.Settings.SaveHotkey ?? base.SaveHotkey;
    public override string CloseHotkey => _appSettingsService?.Settings.CloseHotkey ?? base.CloseHotkey;
    public string PlaybackHotkey => _appSettingsService?.Settings.TogglePlaybackHotkey ?? "Space"; // Specific to Video
    
    public override string RectangleHotkey => _appSettingsService?.Settings.RectangleHotkey ?? base.RectangleHotkey;
    public override string EllipseHotkey => _appSettingsService?.Settings.EllipseHotkey ?? base.EllipseHotkey;
    public override string ArrowHotkey => _appSettingsService?.Settings.ArrowHotkey ?? base.ArrowHotkey;
    public override string LineHotkey => _appSettingsService?.Settings.LineHotkey ?? base.LineHotkey;
    public override string PenHotkey => _appSettingsService?.Settings.PenHotkey ?? base.PenHotkey;
    public override string TextHotkey => _appSettingsService?.Settings.TextHotkey ?? base.TextHotkey;
    public override string MosaicHotkey => _appSettingsService?.Settings.MosaicHotkey ?? base.MosaicHotkey;
    public override string BlurHotkey => _appSettingsService?.Settings.BlurHotkey ?? base.BlurHotkey;

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
    public string MosaicTooltip => $"{LocalizationService.Instance["TipMosaic"]} ({MosaicHotkey})";
    public string BlurTooltip => $"{LocalizationService.Instance["TipBlur"]} ({BlurHotkey})";
    public string PlaybackTooltip => $"{LocalizationService.Instance["ActionPlayback"]} ({PlaybackHotkey})";
    public string ToggleToolbarTooltip => $"{LocalizationService.Instance["ActionToolbar"]} ({_appSettingsService?.Settings.ToggleToolbarHotkey ?? "H"})";
    public string CloseTooltip => $"{LocalizationService.Instance["ActionClose"]} ({CloseHotkey})";
    public string RepeatTooltip => $"{LocalizationService.Instance["ActionRepeat"]}";
    public string SelectionTooltip => $"{LocalizationService.Instance["TipSelectionArea"]} (S)";
    public string CropTooltip => $"{LocalizationService.Instance["TipCrop"]} (C)";
    public string PinSelectionTooltip => $"{LocalizationService.Instance["TipPinSelection"]} (F3)";

    public override Avalonia.Thickness WindowPadding
    {
        get
        {
            // If decorations are hidden, we just need the standard margin (e.g. 10 for shadow/resize handles).
            // If they are visible, we need enough space for the wings (WingWidth).
            double hPad = HidePinDecoration ? 10 : System.Math.Max(10, WingWidth);
            double vPad = 25;
            
            // RESERVE space for floating toolbar if visible
            // Two rows: Toolbar Height(32*2) + Spacing(4) + Bottom Margin(10) = 78px
            double bottomPad = vPad;
            if (ShowToolbar) bottomPad += 78;
            
            return new Avalonia.Thickness(hPad, vPad, hPad, bottomPad);
        }
    }


    // Dependencies
    private readonly GimmeCapture.Services.Abstractions.IClipboardService _clipboardService;
    public GimmeCapture.Services.Abstractions.IClipboardService ClipboardService => _clipboardService;
    private readonly AppSettingsService? _appSettingsService;

    // Actions & Commands
    public System.Func<Task>? CopyAction { get; set; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CropCommand { get; private set; } = null!; // Future implementation
    public ReactiveCommand<Unit, Unit> PinSelectionCommand { get; private set; } = null!; // Future implementation

    // Annotation Proxies
    public bool CanUndo => HasUndo;
    public bool CanRedo => HasRedo;

    public FloatingVideoViewModel(string videoPath, string ffmpegPath, int width, int height, double originalWidth, double originalHeight, Avalonia.Media.Color borderColor, double borderThickness, bool hideDecoration, bool hideBorder, GimmeCapture.Services.Abstractions.IClipboardService clipboardService, AppSettingsService? appSettingsService)
    {
        VideoPath = videoPath;
        _ffmpegPath = ffmpegPath;
        _width = (width / 2) * 2; // Ensure even for FFmpeg
        _height = (height / 2) * 2;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
        DisplayWidth = originalWidth;
        DisplayHeight = originalHeight;
        BorderColor = borderColor;
        BorderThickness = borderThickness;
        HidePinDecoration = hideDecoration;
        HidePinBorder = hideBorder;
        _clipboardService = clipboardService;
        _appSettingsService = appSettingsService;

        // Apply Default Toolbar Visibility
        ShowToolbar = !(_appSettingsService?.Settings.DefaultHideRecordToolbar ?? false);

        InitializeBaseCommands();
        InitializeActionCommands();
        // InitializeToolbarCommands(); // Handled by Base
        InitializeAnnotationCommands();
        InitializeMediaCommands(); // Media init last as it starts playback
    }

    public override void Dispose()
    {
        var cts = _playCts;
        if (cts != null)
        {
            Task.Run(() => { try { cts.Cancel(); } catch { } });
        }
        _playCts?.Dispose();
        VideoBitmap?.Dispose();
    }
}
