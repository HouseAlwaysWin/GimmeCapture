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

public partial class SnipWindowViewModel : ViewModelBase, IDisposable, IDrawingToolViewModel
{
    private readonly RecordingService? _recordingService;
    public RecordingService? RecordingService => _recordingService;
    private readonly MainWindowViewModel? _mainVm;
    public MainWindowViewModel? MainVm => _mainVm;
    private readonly IScreenCaptureService _captureService;
    private readonly CompositeDisposable _disposables = new();

    // Hotkeys / Tooltips
    public string SnipHotkey => _mainVm?.SnipHotkey ?? "F1";
    public string RecordHotkey => _mainVm?.RecordHotkey ?? "F2";
    public string PinHotkey => _mainVm?.PinHotkey ?? "F3";
    public string CopyHotkey => _mainVm?.CopyHotkey ?? "Ctrl+C";
    public string UndoHotkey => _mainVm?.UndoHotkey ?? "Ctrl+Z";
    public string RedoHotkey => _mainVm?.RedoHotkey ?? "Ctrl+Y";
    public string ClearHotkey => _mainVm?.ClearHotkey ?? "Delete";
    public string SaveHotkey => _mainVm?.SaveHotkey ?? "Ctrl+S";
    public string CloseHotkey => _mainVm?.CloseHotkey ?? "Escape";
    public string RectangleHotkey => _mainVm?.RectangleHotkey ?? "R";
    public string EllipseHotkey => _mainVm?.EllipseHotkey ?? "E";
    public string ArrowHotkey => _mainVm?.ArrowHotkey ?? "A";
    public string LineHotkey => _mainVm?.LineHotkey ?? "L";
    public string PenHotkey => _mainVm?.PenHotkey ?? "P";
    public string TextHotkey => _mainVm?.TextHotkey ?? "T";
    public string MosaicHotkey => _mainVm?.MosaicHotkey ?? "M";
    public string BlurHotkey => _mainVm?.BlurHotkey ?? "B";

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
    public string SnipTooltip => $"{LocalizationService.Instance["CaptureModeNormal"]} ({SnipHotkey})";
    public string RecordTooltip => $"{LocalizationService.Instance["CaptureModeRecord"]} ({RecordHotkey})";

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

        if (_recordingService != null)
        {
            _recordingService.WhenAnyValue(x => x.State)
                .Subscribe(_ => 
                {
                    this.RaisePropertyChanged(nameof(RecState));
                    this.RaisePropertyChanged(nameof(IsRecordingActive));
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
            InitializeSAM2(mainVm);
            
            // Sync translation activation with global settings in real-time


            // Sync AI Scan Box visibility
            mainVm.WhenAnyValue(x => x.ShowAIScanBox)
                  .Subscribe(val => ShowAIScanBox = val)
                  .DisposeWith(_disposables);

            // Sync Enable AI Scan
            mainVm.WhenAnyValue(x => x.EnableAIScan)
                  .Subscribe(val => EnableAIScan = val)
                  .DisposeWith(_disposables);
        }

        UpdateMask();
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
