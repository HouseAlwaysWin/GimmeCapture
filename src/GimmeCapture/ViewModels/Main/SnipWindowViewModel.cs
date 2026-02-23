using Avalonia;
using Avalonia.Media;
using System.Windows.Input;
using GimmeCapture.Models;
using ReactiveUI;
using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;
using GimmeCapture.ViewModels.Shared;
using System.Reactive.Disposables;

namespace GimmeCapture.ViewModels.Main;

public enum SnipState { Idle, Detecting, Selecting, Selected }
public enum SnipMode { Screenshot, Recording, Translation }

public partial class SnipWindowViewModel : ViewModelBase, IDisposable, IDrawingToolViewModel
{
    private readonly RecordingService? _recordingService;
    public RecordingService? RecordingService => _recordingService;
    private readonly MainWindowViewModel? _mainVm;
    public MainWindowViewModel? MainVm => _mainVm;
    private readonly IScreenCaptureService _captureService;
    private readonly CompositeDisposable _disposables = new();

    private SnipMode _currentMode = SnipMode.Screenshot;
    public SnipMode CurrentMode
    {
        get => _currentMode;
        set => SetCurrentMode(value);
    }

    // Hotkeys / Tooltips (Dynamic by Mode)
    public string SnipHotkey => _mainVm?.SnipHotkey ?? "F1";
    public string RecordHotkey => _mainVm?.RecordHotkey ?? "F2";
    public string TranslateHotkey => _mainVm?.TranslateHotkey ?? "F3";
    
    public string CopyHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordCopyHotkey ?? "Ctrl+C") : (_mainVm?.SnipCopyHotkey ?? "Ctrl+C");
    public string UndoHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordUndoHotkey ?? "Ctrl+Z") : (_mainVm?.SnipUndoHotkey ?? "Ctrl+Z");
    public string RedoHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordRedoHotkey ?? "Ctrl+Y") : (_mainVm?.SnipRedoHotkey ?? "Ctrl+Y");
    public string ClearHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordClearHotkey ?? "Delete") : (_mainVm?.SnipClearHotkey ?? "Delete");
    public string SaveHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordSaveHotkey ?? "Ctrl+S") : (_mainVm?.SnipSaveHotkey ?? "Ctrl+S");
    public string CloseHotkey => CurrentMode == SnipMode.Translation ? (_mainVm?.TranslateCloseHotkey ?? "Escape") : (CurrentMode == SnipMode.Recording ? (_mainVm?.RecordCloseHotkey ?? "Escape") : (_mainVm?.SnipCloseHotkey ?? "Escape"));
    
    public string RectangleHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordRectangleHotkey ?? "R") : (_mainVm?.SnipRectangleHotkey ?? "R");
    public string EllipseHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordEllipseHotkey ?? "E") : (_mainVm?.SnipEllipseHotkey ?? "E");
    public string ArrowHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordArrowHotkey ?? "A") : (_mainVm?.SnipArrowHotkey ?? "A");
    public string LineHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordLineHotkey ?? "L") : (_mainVm?.SnipLineHotkey ?? "L");
    public string PenHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordPenHotkey ?? "P") : (_mainVm?.SnipPenHotkey ?? "P");
    public string TextHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordTextHotkey ?? "T") : (_mainVm?.SnipTextHotkey ?? "T");
    public string MosaicHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordMosaicHotkey ?? "M") : (_mainVm?.SnipMosaicHotkey ?? "M");
    public string BlurHotkey => CurrentMode == SnipMode.Recording ? (_mainVm?.RecordBlurHotkey ?? "B") : (_mainVm?.SnipBlurHotkey ?? "B");

    public string ActiveActionHotkey => CurrentMode == SnipMode.Translation ? (_mainVm?.TranslateActionHotkey ?? "F3") : (CurrentMode == SnipMode.Recording ? (_mainVm?.RecordActionHotkey ?? "F3") : (_mainVm?.SnipPinHotkey ?? "F3"));
    public string ActiveToolbarHotkey => CurrentMode == SnipMode.Translation ? (_mainVm?.TranslateToolbarHotkey ?? "F4") : (CurrentMode == SnipMode.Recording ? (_mainVm?.RecordToolbarHotkey ?? "F4") : (_mainVm?.SnipToolbarHotkey ?? "F4"));
    public string ActivePlaybackHotkey => _mainVm?.RecordPlaybackHotkey ?? "Space";

    public string UndoTooltip => $"{LocalizationService.Instance["Undo"]} ({UndoHotkey})";
    public string RedoTooltip => $"{LocalizationService.Instance["Redo"]} ({RedoHotkey})";
    public string ClearTooltip => $"{LocalizationService.Instance["Clear"]} ({ClearHotkey})";
    public string SaveTooltip => $"{LocalizationService.Instance["TipSave"]} ({SaveHotkey})";
    public string CopyTooltip => $"{LocalizationService.Instance["TipCopy"]} ({CopyHotkey})";
    public string PinTooltip => CurrentMode == SnipMode.Translation ? string.Empty : (CurrentMode == SnipMode.Recording ? $"{LocalizationService.Instance["ActionStartPin"]} ({ActiveActionHotkey})" : $"{LocalizationService.Instance["TipPin"]} ({ActiveActionHotkey})");
    public string RectangleTooltip => $"{LocalizationService.Instance["TipRectangle"]} ({RectangleHotkey})";
    public string EllipseTooltip => $"{LocalizationService.Instance["TipEllipse"]} ({EllipseHotkey})";
    public string ArrowTooltip => $"{LocalizationService.Instance["TipArrow"]} ({ArrowHotkey})";
    public string LineTooltip => $"{LocalizationService.Instance["TipLine"]} ({LineHotkey})";
    public string PenTooltip => $"{LocalizationService.Instance["TipPen"]} ({PenHotkey})";
    public string TextTooltip => $"{LocalizationService.Instance["TipText"]} ({TextHotkey})";
    public string MosaicTooltip => $"{LocalizationService.Instance["TipMosaic"]} ({MosaicHotkey})";
    public string BlurTooltip => $"{LocalizationService.Instance["TipBlur"]} ({BlurHotkey})";
    public string SnipTooltip => $"{LocalizationService.Instance["CaptureModeNormal"]} ({SnipHotkey})";
    public string RecordTooltip => $"{LocalizationService.Instance["CaptureModeRecord"]} ({RecordHotkey})";
    public string HideTranslationResultsTooltip => $"{LocalizationService.Instance["HideTranslationResults"]} ({ActiveActionHotkey})";
    public string ToggleToolbarTooltip => $"{LocalizationService.Instance["ActionToolbar"]} ({ActiveToolbarHotkey})";
    public string TogglePlaybackTooltip => $"{LocalizationService.Instance["ActionPlayback"]} ({ActivePlaybackHotkey})";

    public Color ThemeColor => _mainVm?.ThemeColor ?? Colors.Red;
    public Color ThemeDeepColor 
    {
        get
        {
            if (ThemeColor == Color.Parse("#D4AF37")) return Color.Parse("#8B7500");
            if (ThemeColor == Color.Parse("#E0E0E0")) return Color.Parse("#606060");
            return Color.Parse("#900000");
        }
    }

    public bool IsAIDownloading => _mainVm?.AIResourceService.IsDownloading ?? false;
    public double AIResourceProgress => _mainVm?.AIResourceService.DownloadProgress ?? 0;

    // Actions
    public Action? CloseAction { get; set; }
    public Action? HideAction { get; set; }
    public Action? ShowAction { get; set; }
    public Action? OpenRecordingProgressWindowAction { get; set; }
    public Action? CloseRecordingProgressWindowAction { get; set; }
    public Action? SaveAction { get; set; }
    public Action? FocusWindowAction { get; set; }
    public Action<Avalonia.Media.Imaging.Bitmap, Rect, Color, double, bool>? OpenPinWindowAction { get; set; }
    public Func<Task<string?>>? PickSaveFileAction { get; set; }

    public static class StaticData
    {
        public static Color[] ColorsList { get; } = new[]
        {
            Colors.Red, Colors.Green, Colors.Blue, 
            Colors.Yellow, Colors.Cyan, Colors.Magenta,
            Colors.White, Colors.Black, Colors.Gray
        };
    }
    public IEnumerable<Color> PresetColors => StaticData.ColorsList;

    public SnipWindowViewModel() : this(Colors.Red, 2.0, 0.5, null, null) { }

    public SnipWindowViewModel(Color borderColor, double borderThickness, double maskOpacity, RecordingService? recService = null, MainWindowViewModel? mainVm = null)
    {
        _captureService = new WindowsScreenCaptureService();
        _selectionBorderColor = borderColor;
        _selectionBorderThickness = borderThickness;
        _maskOpacity = maskOpacity;
        _recordingService = recService;
        _mainVm = mainVm;

        if (_mainVm != null)
        {
            // Always use ThemeColor for all UI boxes per request
            _selectionBorderColor = _mainVm.ThemeColor;
            _selectionBorderThickness = _mainVm.BorderThickness;
            _maskOpacity = _mainVm.MaskOpacity;
        }

        if (_recordingService != null)
        {
            _recordingService.WhenAnyValue(x => x.State)
                .Subscribe(_ => 
                {
                    this.RaisePropertyChanged(nameof(RecState));
                    this.RaisePropertyChanged(nameof(IsRecordingActive));
                    this.RaisePropertyChanged(nameof(HideFrameBorder));
                    this.RaisePropertyChanged(nameof(HideSelectionDecoration));
                }).DisposeWith(_disposables);

            _recordingService.WhenAnyValue(x => x.IsFinalizing)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(isFinalizing => 
                {
                    IsRecordingFinalizing = isFinalizing;
                    if (isFinalizing)
                    {
                         ProcessingText = LocalizationService.Instance["StatusProcessing"];
                         OpenRecordingProgressWindowAction?.Invoke();
                    }
                    else
                    {
                         CloseRecordingProgressWindowAction?.Invoke();
                    }
                }).DisposeWith(_disposables);
        }

        InitializeActionCommands();
        InitializeToolbarCommands();
        InitializeSelectionCommands();
        if (mainVm != null)
        {
            // 只在 AI 功能啟用時才預載 SAM2 模型，避免不必要的記憶體消耗
            if (mainVm.EnableAI)
            {
                InitializeSAM2(mainVm);
            }
            
            // Sync translation activation with global settings in real-time


            // Sync AI Scan Box visibility
            mainVm.WhenAnyValue(x => x.ShowAIScanBox)
                  .Subscribe(val => ShowAIScanBox = val)
                  .DisposeWith(_disposables);

            // Sync Enable AI Scan
            mainVm.WhenAnyValue(x => x.EnableAIScan)
                  .Subscribe(val => EnableAIScan = val)
                  .DisposeWith(_disposables);

            // Sync AI Download Progress
            mainVm.WhenAnyValue(x => x.ProgressValue)
                  .Subscribe(val => ProgressValue = val)
                  .DisposeWith(_disposables);

            // Sync IsIndeterminate
            mainVm.WhenAnyValue(x => x.IsIndeterminate)
                  .Subscribe(val => IsIndeterminate = val)
                  .DisposeWith(_disposables);

            // Sync ShowProcessingOverlay — only propagate 'true' from MainVM.
            // When MainVM goes false, only clear overlay if no local operation is active.
            mainVm.WhenAnyValue(x => x.ShowProcessingOverlay)
                  .Subscribe(val =>
                  {
                      if (val)
                          ShowProcessingOverlay = true;
                      else if (!_isLocalProcessing)
                          ShowProcessingOverlay = false;
                  })
                  .DisposeWith(_disposables);

            // Sync Theme Color (Unified for all boxes)
            mainVm.WhenAnyValue(x => x.ThemeColor)
                  .Subscribe(val => 
                  {
                      SelectionBorderColor = val;
                      this.RaisePropertyChanged(nameof(ThemeColor));
                      this.RaisePropertyChanged(nameof(ThemeDeepColor));
                  })
                  .DisposeWith(_disposables);

            // Sync Border Thickness
            mainVm.WhenAnyValue(x => x.BorderThickness)
                  .Subscribe(val => SelectionBorderThickness = val)
                  .DisposeWith(_disposables);

            // Sync Mask Opacity
            mainVm.WhenAnyValue(x => x.MaskOpacity)
                  .Subscribe(val => MaskOpacity = val)
                  .DisposeWith(_disposables);
        }

        // Initialize Debug Compatibility
        _isTopmost = !System.Diagnostics.Debugger.IsAttached;
        _isMaskVisible = true;
        
        if (System.Diagnostics.Debugger.IsAttached)
        {
            Console.WriteLine("[SnipWindow] Debugger detected. IsTopmost = false. Press Ctrl+Alt+T to toggle.");
        }

        // Real-time sync for decoration scales from MainVM
        if (mainVm != null)
        {
            mainVm.WhenAnyValue(x => x.WingScale)
                  .Subscribe(val => {
                      this.RaisePropertyChanged(nameof(WingScale));
                      this.RaisePropertyChanged(nameof(WingWidth));
                      this.RaisePropertyChanged(nameof(WingHeight));
                      this.RaisePropertyChanged(nameof(LeftWingMargin));
                      this.RaisePropertyChanged(nameof(RightWingMargin));
                  })
                  .DisposeWith(_disposables);
                  
            mainVm.WhenAnyValue(x => x.CornerIconScale)
                  .Subscribe(val => {
                      this.RaisePropertyChanged(nameof(CornerIconScale));
                      this.RaisePropertyChanged(nameof(SelectionIconSize));
                  })
                  .DisposeWith(_disposables);
        }

        // Reactive toolbar positioning for translation mode
        this.WhenAnyValue(x => x.ViewportSize, x => x.ToolbarWidth, x => x.CurrentMode, x => x.ActiveScreenBounds)
            .Subscribe(_ => 
            {
                if (CurrentMode == SnipMode.Translation)
                {
                    InitializeTranslationToolbarPosition();
                }
            })
            .DisposeWith(_disposables);

        UpdateMask();
    }

    private bool _showToolbar = true;
    public bool ShowToolbar
    {
        get => _showToolbar;
        set 
        {
            this.RaiseAndSetIfChanged(ref _showToolbar, value);
            this.RaisePropertyChanged(nameof(IsToolbarVisible));
        }
    }

    private bool _showTranslationResults = true;
    public bool ShowTranslationResults
    {
        get => _showTranslationResults;
        set => this.RaiseAndSetIfChanged(ref _showTranslationResults, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> ToggleTranslationResultsCommand { get; protected set; } = null!;

    private bool _isCapturing;
    public bool IsCapturing
    {
        get => _isCapturing;
        set => this.RaiseAndSetIfChanged(ref _isCapturing, value);
    }

    private bool _isTopmost = true;
    public bool IsTopmost
    {
        get => _isTopmost;
        set => this.RaiseAndSetIfChanged(ref _isTopmost, value);
    }

    private bool _isMaskVisible = true;
    public bool IsMaskVisible
    {
        get => _isMaskVisible;
        set => this.RaiseAndSetIfChanged(ref _isMaskVisible, value);
    }

    private bool _isIndeterminate;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => this.RaiseAndSetIfChanged(ref _isIndeterminate, value);
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set => this.RaiseAndSetIfChanged(ref _progressValue, value);
    }

    private string _processingText = string.Empty;
    public string ProcessingText
    {
        get => _processingText;
        set => this.RaiseAndSetIfChanged(ref _processingText, value);
    }

    private bool _isLocalProcessing;
    
    private bool _showProcessingOverlay;
    public bool ShowProcessingOverlay
    {
        get => _showProcessingOverlay;
        set => this.RaiseAndSetIfChanged(ref _showProcessingOverlay, value);
    }

    private bool _isInputFocused;
    public bool IsInputFocused
    {
        get => _isInputFocused;
        set => this.RaiseAndSetIfChanged(ref _isInputFocused, value);
    }

    public void Dispose()
    {
        _disposables.Dispose();
        _sam2Service?.Dispose();
        _recordTimer?.Stop();
        
        CloseAction = null;
        HideAction = null;
        ShowAction = null;
        OpenRecordingProgressWindowAction = null;
        CloseRecordingProgressWindowAction = null;
        FocusWindowAction = null;
        PickSaveFileAction = null;
    }
}
