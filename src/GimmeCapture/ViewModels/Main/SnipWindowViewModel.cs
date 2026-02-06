using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;
using System.ComponentModel;
using System.IO;
using System.Reactive.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GimmeCapture.Models;
using System.Linq;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.ViewModels.Shared;
using GimmeCapture.Views.Floating;

namespace GimmeCapture.ViewModels.Main;

public enum SnipState { Idle, Detecting, Selecting, Selected }

public class SnipWindowViewModel : ViewModelBase
{
    private SnipState _currentState = SnipState.Detecting;
    public SnipState CurrentState
    {
        get => _currentState;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentState, value);
            if (value == SnipState.Selected && AutoActionMode > 0 && AutoActionMode < 3)
            {
                TriggerAutoAction();
            }
        }
    }
    private bool _isRecordingMode;
    public bool IsRecordingMode
    {
        get => _isRecordingMode;
        set 
        {
            this.RaiseAndSetIfChanged(ref _isRecordingMode, value);
            
            // Update border color based on mode
            if (value)
            {
                SelectionBorderColor = Color.Parse("#FFD700"); // Gold for Recording
            }
            else
            {
                SelectionBorderColor = _mainVm?.BorderColor ?? Colors.Red;
            }
            
            this.RaisePropertyChanged(nameof(HideFrameBorder));
            this.RaisePropertyChanged(nameof(HideSelectionDecoration));
            this.RaisePropertyChanged(nameof(ModeDisplayName));
        }
    }

    public string ModeDisplayName => IsRecordingMode 
        ? LocalizationService.Instance["CaptureModeRecord"] 
        : LocalizationService.Instance["CaptureModeNormal"];

    // True when actively recording (not idle, not paused) - used to hide selection border
    public bool IsRecordingActive => _recordingService?.State == RecordingState.Recording;

    // Current recording format (gif, mp4, webm, etc.)
    public string RecordFormat => _mainVm?.RecordFormat ?? "mp4";

    private TimeSpan _recordingDuration = TimeSpan.Zero;
    public TimeSpan RecordingDuration
    {
        get => _recordingDuration;
        set 
        {
            this.RaiseAndSetIfChanged(ref _recordingDuration, value);
            this.RaisePropertyChanged(nameof(RecordingDurationText));
        }
    }

    public string RecordingDurationText => RecordingDuration.ToString(@"mm\:ss");

    private Avalonia.Threading.DispatcherTimer? _recordTimer;
    private readonly RecordingService? _recordingService;
    public RecordingService? RecordingService => _recordingService;
    private readonly MainWindowViewModel? _mainVm;
    public MainWindowViewModel? MainVm => _mainVm;
    public bool HideSelectionDecoration => IsRecordingMode ? (_mainVm?.HideRecordSelectionDecoration ?? false) : (_mainVm?.HideSnipSelectionDecoration ?? false);
    public bool HideFrameBorder => IsRecordingMode ? (_mainVm?.HideRecordSelectionBorder ?? false) : (_mainVm?.HideSnipSelectionBorder ?? false);

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set => this.RaiseAndSetIfChanged(ref _isProcessing, value);
    }

    private string _processingText = "Processing...";
    public string ProcessingText
    {
        get => _processingText;
        set => this.RaiseAndSetIfChanged(ref _processingText, value);
    }

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

    private PixelPoint _screenOffset;
    public PixelPoint ScreenOffset
    {
        get => _screenOffset;
        set => this.RaiseAndSetIfChanged(ref _screenOffset, value);
    }

    private double _visualScaling = 1.0;
    public double VisualScaling
    {
        get => _visualScaling;
        set => this.RaiseAndSetIfChanged(ref _visualScaling, value);
    }

    private Rect _detectedRect;
    public Rect DetectedRect
    {
        get => _detectedRect;
        set => this.RaiseAndSetIfChanged(ref _detectedRect, value);
    }

    public List<Rect> WindowRects { get; set; } = new();
    private readonly WindowDetectionService _detectionService = new();

    public void RefreshWindowRects(IntPtr? excludeHWnd = null)
    {
        // Get global rects (Physical pixels)
        var globalRects = _detectionService.GetVisibleWindowRects(excludeHWnd);
        
        // Translate to local coordinates based on ScreenOffset (Physical)
        // AND convert to logical coordinates by dividing by VisualScaling
        WindowRects = globalRects
            .Select(r => new Rect(
                (r.X - ScreenOffset.X) / VisualScaling, 
                (r.Y - ScreenOffset.Y) / VisualScaling, 
                r.Width / VisualScaling, 
                r.Height / VisualScaling))
            .ToList();
    }

    public void UpdateDetectedRect(Point mousePos)
    {
        if (CurrentState != SnipState.Detecting) return;
        
        var rect = _detectionService.GetRectAtPoint(mousePos, WindowRects);
        
        // Simple heuristic: if the detected rect is basically the whole screen, might be the SnipWindow itself
        // or the desktop. We should be careful about selecting the SnipWindow.
        // But SnipWindow is newly created, should be fine if we filter by size or something if needed.
        
        DetectedRect = rect ?? new Rect(0,0,0,0);
    }

    private int _autoActionMode = 0; // 0=Normal, 1=Copy, 2=Pin
    public int AutoActionMode
    {
        get => _autoActionMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _autoActionMode, value);
            if (value > 0 && CurrentState == SnipState.Selected)
            {
                TriggerAutoAction();
            }
        }
    }

    private void TriggerAutoAction()
    {
        if (AutoActionMode == 1) // Copy
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () => await Copy());
        }
        else if (AutoActionMode == 2) // Pin
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(async () => await Pin());
        }
    }

    private Size _viewportSize;
    public Size ViewportSize
    {
        get => _viewportSize;
        set 
        {
            this.RaiseAndSetIfChanged(ref _viewportSize, value);
            UpdateToolbarPosition();
        }
    }

    private double _toolbarTop;
    public double ToolbarTop
    {
        get => _toolbarTop;
        set => this.RaiseAndSetIfChanged(ref _toolbarTop, value);
    }

    private double _toolbarLeft;
    public double ToolbarLeft
    {
        get => _toolbarLeft;
        set => this.RaiseAndSetIfChanged(ref _toolbarLeft, value);
    }

    private Rect _selectionRect;
    public Rect SelectionRect
    {
        get => _selectionRect;
        set 
        {
            this.RaiseAndSetIfChanged(ref _selectionRect, value);
            UpdateMask();
            UpdateToolbarPosition();
        }
    }

    private void UpdateToolbarPosition()
    {
        // Use ViewportSize if available, otherwise assume a standard FHD height for initial positioning
        double vh = ViewportSize.Height > 0 ? ViewportSize.Height : 1080;
        double vw = ViewportSize.Width > 0 ? ViewportSize.Width : 1920;

        const double toolbarHeight = 35; // More accurate estimated height
        const double toolbarWidth = 400; // Estimated max width

        // Position below by default
        double top = SelectionRect.Bottom + 4; 
        double left = SelectionRect.Left;

        // If bottom overflows, position above selection
        if (top + toolbarHeight > vh - 10)
        {
            top = SelectionRect.Top - toolbarHeight - 4;
        }

        // Final safety clamps to keep toolbar within viewport
        if (top < 4) top = 4;
        if (top + toolbarHeight > vh - 4) top = vh - toolbarHeight - 4;

        if (left + toolbarWidth > vw - 10)
        {
            left = vw - toolbarWidth - 10;
        }
        if (left < 10) left = 10;

        ToolbarTop = top;
        ToolbarLeft = left;
    }

    private void UpdateMask()
    {
        MaskGeometry = new CombinedGeometry
        {
            GeometryCombineMode = GeometryCombineMode.Exclude,
            Geometry1 = new RectangleGeometry(new Rect(-10000, -10000, 20000, 20000)),
            Geometry2 = new RectangleGeometry(SelectionRect)
        };
    }

    private Geometry _maskGeometry = new GeometryGroup();
    public Geometry MaskGeometry
    {
        get => _maskGeometry;
        set => this.RaiseAndSetIfChanged(ref _maskGeometry, value);
    }

    private bool _isMagnifierEnabled = true;
    public bool IsMagnifierEnabled
    {
        get => _isMagnifierEnabled;
        set => this.RaiseAndSetIfChanged(ref _isMagnifierEnabled, value);
    }

    private readonly IScreenCaptureService _captureService;

    // Commands
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> PinCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleModeCommand { get; }
    public ReactiveCommand<bool, Unit> SetCaptureModeCommand { get; }
    public ReactiveCommand<Unit, Unit> StartRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> PauseRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> StopRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyRecordingCommand { get; }
    public ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; }
    public ReactiveCommand<string, Unit> ToggleToolGroupCommand { get; }
    public ReactiveCommand<Color, Unit> ChangeColorCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveBackgroundCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseFontSizeCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyHexColorCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangeLanguageCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleBoldCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleItalicCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; }

    private readonly System.Collections.Generic.List<Annotation> _redoStack = new();
    private bool _isUndoingOrRedoing = false;

    private void Undo()
    {
        if (Annotations.Count > 0)
        {
            var item = Annotations[Annotations.Count - 1];
            _isUndoingOrRedoing = true;
            Annotations.RemoveAt(Annotations.Count - 1);
            _redoStack.Add(item);
            _isUndoingOrRedoing = false;
        }
    }

    private void Redo()
    {
        if (_redoStack.Count > 0)
        {
            var item = _redoStack[_redoStack.Count - 1];
            _isUndoingOrRedoing = true;
            _redoStack.RemoveAt(_redoStack.Count - 1);
            Annotations.Add(item);
            _isUndoingOrRedoing = false;
        }
    }

    // Annotation Properties
    public ObservableCollection<Annotation> Annotations { get; } = new();

    private AnnotationType _currentTool = AnnotationType.None;
    public AnnotationType CurrentTool
    {
        get => _currentTool;
        set 
        {
            this.RaiseAndSetIfChanged(ref _currentTool, value);
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsLineToolActive));
            this.RaisePropertyChanged(nameof(IsTextToolActive));
        }
    }

    public bool IsAIDownloading => _mainVm?.AIResourceService.IsDownloading ?? false;
    public double AIResourceProgress => _mainVm?.AIResourceService.DownloadProgress ?? 0;

    public bool IsShapeToolActive => CurrentTool == AnnotationType.Rectangle || CurrentTool == AnnotationType.Ellipse;
    public bool IsLineToolActive => CurrentTool == AnnotationType.Arrow || CurrentTool == AnnotationType.Line || CurrentTool == AnnotationType.Pen;
    public bool IsTextToolActive => CurrentTool == AnnotationType.Text;

    public void ToggleToolGroup(string group)
    {
        if (group == "Shapes")
        {
            if (IsShapeToolActive)
            {
                CurrentTool = AnnotationType.None;
                IsDrawingMode = false;
            }
        }
        else if (group == "Lines")
        {
            if (IsLineToolActive)
            {
                CurrentTool = AnnotationType.None;
                IsDrawingMode = false;
            }
        }
        else if (group == "Text")
        {
            if (IsTextToolActive)
            {
                CurrentTool = AnnotationType.None;
                IsDrawingMode = false;
            }
            else
            {
                CurrentTool = AnnotationType.Text;
                IsDrawingMode = true;
            }
        }
    }

    private Color _selectedColor = Colors.Red;
    public Color SelectedColor
    {
        get => _selectedColor;
        set => this.RaiseAndSetIfChanged(ref _selectedColor, value);
    }

    private string _customHexColor = "#FF0000";
    public string CustomHexColor
    {
        get => _customHexColor;
        set => this.RaiseAndSetIfChanged(ref _customHexColor, value);
    }

    private double _currentThickness = 2.0;
    public double CurrentThickness
    {
        get => _currentThickness;
        set => this.RaiseAndSetIfChanged(ref _currentThickness, value);
    }

    private double _currentFontSize = 24.0;
    public double CurrentFontSize
    {
        get => _currentFontSize;
        set => this.RaiseAndSetIfChanged(ref _currentFontSize, value);
    }

    private string _currentFontFamily = "Arial";
    public string CurrentFontFamily
    {
        get => _currentFontFamily;
        set => this.RaiseAndSetIfChanged(ref _currentFontFamily, value);
    }

    private bool _isBold;
    public bool IsBold
    {
        get => _isBold;
        set => this.RaiseAndSetIfChanged(ref _isBold, value);
    }

    private bool _isItalic;
    public bool IsItalic
    {
        get => _isItalic;
        set => this.RaiseAndSetIfChanged(ref _isItalic, value);
    }

    public ObservableCollection<string> AvailableFonts { get; } = new ObservableCollection<string>
    {
        "Arial", "Segoe UI", "Consolas", "Times New Roman", "Comic Sans MS", "Microsoft JhengHei", "Meiryo"
    };

    private bool _isBackgroundRemoved;
    public bool IsBackgroundRemoved
    {
        get => _isBackgroundRemoved;
        set => this.RaiseAndSetIfChanged(ref _isBackgroundRemoved, value);
    }

    private bool _isDrawingMode = false;
    public bool IsDrawingMode
    {
        get => _isDrawingMode;
        set
        {
            if (value && !_isDrawingMode)
            {
                // Entering drawing mode - capture snapshot
                // We do this async. Since setter is sync, we fire and forget or use Dispatcher.
                // However, we want the snapshot to be ready.
                // Ideally this should be a Command, but IsDrawingMode is property bound.
                // We'll trigger the capture.
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => 
                {
                    try
                    {
                        var snapshot = await _captureService.CaptureRegionBitmapAsync(SelectionRect, ScreenOffset, VisualScaling);
                        if (snapshot != null)
                        {
                            // Dispose old if exists
                            if (DrawingModeSnapshot != null) DrawingModeSnapshot.Dispose();
                            DrawingModeSnapshot = snapshot;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Capture failed: {ex}");
                    }
                });
            }
            else if (!value && _isDrawingMode)
            {
                // Exiting drawing mode - clear and dispose snapshot
                if (_drawingModeSnapshot != null)
                {
                    var temp = _drawingModeSnapshot;
                    DrawingModeSnapshot = null;
                    temp.Dispose();
                }
            }
            this.RaiseAndSetIfChanged(ref _isDrawingMode, value);
        }
    }

    // Removed CaptureDrawingModeSnapshotAction as logic is now in ViewModel/Service

    private Avalonia.Media.Imaging.Bitmap? _drawingModeSnapshot;
    /// <summary>
    /// Snapshot of the selection area captured when entering drawing mode.
    /// Displayed as background so user can see what they're annotating.
    /// </summary>
    public Avalonia.Media.Imaging.Bitmap? DrawingModeSnapshot
    {
        get => _drawingModeSnapshot;
        set => this.RaiseAndSetIfChanged(ref _drawingModeSnapshot, value);
    }

    private bool _isEnteringText = false;
    public bool IsEnteringText
    {
        get => _isEnteringText;
        set => this.RaiseAndSetIfChanged(ref _isEnteringText, value);
    }
    
    private Point _textInputPosition;
    public Point TextInputPosition
    {
        get => _textInputPosition;
        set => this.RaiseAndSetIfChanged(ref _textInputPosition, value);
    }

    private string _pendingText = string.Empty;
    public string PendingText
    {
        get => _pendingText;
        set => this.RaiseAndSetIfChanged(ref _pendingText, value);
    }

    private Color _selectionBorderColor = Colors.Red;
    public Color SelectionBorderColor
    {
        get => _selectionBorderColor;
        set => this.RaiseAndSetIfChanged(ref _selectionBorderColor, value);
    }

    private double _selectionBorderThickness = 2.0;
    public double SelectionBorderThickness
    {
        get => _selectionBorderThickness;
        set => this.RaiseAndSetIfChanged(ref _selectionBorderThickness, value);
    }

    public double WingScale
    {
        get => _mainVm?.WingScale ?? 1.0;
        set 
        {
            if (_mainVm != null)
            {
                _mainVm.WingScale = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(WingWidth));
                this.RaisePropertyChanged(nameof(WingHeight));
                this.RaisePropertyChanged(nameof(LeftWingMargin));
                this.RaisePropertyChanged(nameof(RightWingMargin));
            }
        }
    }

    public double CornerIconScale
    {
        get => _mainVm?.CornerIconScale ?? 1.0;
        set
        {
            if (_mainVm != null)
            {
                _mainVm.CornerIconScale = value;
                this.RaisePropertyChanged();
                this.RaisePropertyChanged(nameof(SelectionIconSize));
            }
        }
    }

    public double WingWidth => 100 * WingScale;
    public double WingHeight => 60 * WingScale;
    public double SelectionIconSize => 22 * CornerIconScale;
    public Thickness LeftWingMargin => new Thickness(-WingWidth, 0, 0, 0);
    public Thickness RightWingMargin => new Thickness(0, 0, -WingWidth, 0);

    private double _maskOpacity = 0.5;
    public double MaskOpacity
    {
        get => _maskOpacity;
        set => this.RaiseAndSetIfChanged(ref _maskOpacity, value);
    }

    public SnipWindowViewModel() : this(Colors.Red, 2.0, 0.5, null, null) { }

    public SnipWindowViewModel(Color borderColor, double borderThickness, double maskOpacity, RecordingService? recService = null, MainWindowViewModel? mainVm = null)
    {
        // TODO: In real DI, this should be injected. For now we instantiate the concrete Windows implementation.
        // In future Linux support, we would check OS and instantiate LinuxScreenCaptureService.
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
                });

            _recordingService.WhenAnyValue(x => x.IsFinalizing)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(isFinalizing => 
                {
                    if (isFinalizing)
                    {
                         ProcessingText = LocalizationService.Instance["StatusProcessing"] ?? "Processing...";
                    }
                    IsProcessing = isFinalizing;
                });
        }

        // Initial loads
        if (mainVm != null)
        {
            SelectedColor = mainVm.BorderColor;
            
            mainVm.WhenAnyValue(x => x.ThemeColor)
                .Subscribe(_ => {
                    this.RaisePropertyChanged(nameof(ThemeColor));
                    this.RaisePropertyChanged(nameof(ThemeDeepColor));
                });

            /* 
            mainVm.AIResourceService.WhenAnyValue(x => x.IsDownloading)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(isDownloading => 
                {
                    this.RaisePropertyChanged(nameof(IsAIDownloading));
                    if (isDownloading)
                    {
                        ProcessingText = LocalizationService.Instance["ComponentDownloadingProgress"] ?? "Downloading...";
                        IsProcessing = true;
                    }
                    else if (!(_recordingService?.IsFinalizing ?? false))
                    {
                        IsProcessing = false;
                    }
                });

            mainVm.AIResourceService.WhenAnyValue(x => x.DownloadProgress)
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(_ => this.RaisePropertyChanged(nameof(AIResourceProgress)));
            */
        }


        CopyCommand = ReactiveCommand.CreateFromTask(Copy);
        CopyCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        SaveCommand = ReactiveCommand.CreateFromTask(Save);
        SaveCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        PinCommand = ReactiveCommand.CreateFromTask(() => Pin(false));
        PinCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        CloseCommand = ReactiveCommand.Create(Close);
        CloseCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        ToggleModeCommand = ReactiveCommand.Create(() => 
        {
            if (RecState == RecordingState.Idle) IsRecordingMode = !IsRecordingMode;
        });
        ToggleModeCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        SetCaptureModeCommand = ReactiveCommand.Create<bool>(isRecord => 
        {
            if (RecState == RecordingState.Idle) IsRecordingMode = isRecord;
        });
        SetCaptureModeCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        StartRecordingCommand = ReactiveCommand.CreateFromTask(StartRecording);
        StartRecordingCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        PauseRecordingCommand = ReactiveCommand.CreateFromTask(PauseRecording);
        PauseRecordingCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        StopRecordingCommand = ReactiveCommand.CreateFromTask(StopRecording);
        StopRecordingCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        CopyRecordingCommand = ReactiveCommand.CreateFromTask(CopyRecording);
        CopyRecordingCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        Annotations.CollectionChanged += (s, e) =>
        {
            if (!_isUndoingOrRedoing)
            {
                _redoStack.Clear();
            }
        };

        SelectToolCommand = ReactiveCommand.Create<AnnotationType>(t => {
            if (CurrentTool == t)
            {
                CurrentTool = AnnotationType.None;
                IsDrawingMode = false;
            }
            else
            {
                CurrentTool = t;
                IsDrawingMode = true; 
            }
        });
        SelectToolCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        ToggleToolGroupCommand = ReactiveCommand.Create<string>(ToggleToolGroup);
        ToggleToolGroupCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        ChangeColorCommand = ReactiveCommand.Create<Color>(c => SelectedColor = c);
        ChangeColorCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        UndoCommand = ReactiveCommand.Create(Undo);
        UndoCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        RedoCommand = ReactiveCommand.Create(Redo);
        RedoCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        ClearCommand = ReactiveCommand.Create(() => Annotations.Clear());
        
        var canRemoveBackground = this.WhenAnyValue(
            x => x.IsRecordingMode, 
            x => x.IsProcessing, 
            (isRec, isProc) => !isRec && !isProc);

        RemoveBackgroundCommand = ReactiveCommand.CreateFromTask(async () => {
             // Refactored: Pin first, then Run AI
             await Pin(true);
        }, canRemoveBackground);
        RemoveBackgroundCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        IncreaseThicknessCommand = ReactiveCommand.Create(() => { if (CurrentThickness < 20) CurrentThickness += 1; });
        IncreaseThicknessCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        DecreaseThicknessCommand = ReactiveCommand.Create(() => { if (CurrentThickness > 1) CurrentThickness -= 1; });
        DecreaseThicknessCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        IncreaseFontSizeCommand = ReactiveCommand.Create(() => { if (CurrentFontSize < 72) CurrentFontSize += 2; });
        IncreaseFontSizeCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        DecreaseFontSizeCommand = ReactiveCommand.Create(() => { if (CurrentFontSize > 8) CurrentFontSize -= 2; });
        DecreaseFontSizeCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        ApplyHexColorCommand = ReactiveCommand.Create(() => 
        {
            try
            {
                var hex = CustomHexColor.TrimStart('#');
                if (hex.Length == 6)
                {
                    var r = Convert.ToByte(hex.Substring(0, 2), 16);
                    var g = Convert.ToByte(hex.Substring(2, 2), 16);
                    var b = Convert.ToByte(hex.Substring(4, 2), 16);
                    SelectedColor = Color.FromRgb(r, g, b);
                }
            }
            catch { }
        });
        ApplyHexColorCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        ChangeLanguageCommand = ReactiveCommand.Create(() => LocalizationService.Instance.CycleLanguage());
        ChangeLanguageCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        ToggleBoldCommand = ReactiveCommand.Create<Unit, bool>(_ => IsBold = !IsBold);
        ToggleBoldCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        ToggleItalicCommand = ReactiveCommand.Create<Unit, bool>(_ => IsItalic = !IsItalic);
        ToggleItalicCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        IncreaseWingScaleCommand = ReactiveCommand.Create(() => { if (WingScale < 3.0) WingScale = Math.Round(WingScale + 0.1, 1); });
        IncreaseWingScaleCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        DecreaseWingScaleCommand = ReactiveCommand.Create(() => { if (WingScale > 0.5) WingScale = Math.Round(WingScale - 0.1, 1); });
        DecreaseWingScaleCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        IncreaseCornerIconScaleCommand = ReactiveCommand.Create(() => { if (CornerIconScale < 1.0) CornerIconScale = Math.Round(CornerIconScale + 0.1, 1); });
        IncreaseCornerIconScaleCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        DecreaseCornerIconScaleCommand = ReactiveCommand.Create(() => { if (CornerIconScale > 0.4) CornerIconScale = Math.Round(CornerIconScale - 0.1, 1); });
        DecreaseCornerIconScaleCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        UpdateMask();
    }

    /// <summary>
    /// Handles right-click logic:
    /// - If Recording: Do nothing (handled by UI to prevent interruptions)
    /// - If Selecting/Selected: Reset to Detecting
    /// - Otherwise: Close Window
    /// </summary>
    public void HandleRightClick()
    {
        if (RecState != RecordingState.Idle) return;

        if (CurrentState == SnipState.Selecting || CurrentState == SnipState.Selected)
        {
            CurrentState = SnipState.Detecting;
            SelectionRect = new Rect(0,0,0,0);
        }
        else
        {
            Close();
        }
    }

    public RecordingState RecState => _recordingService?.State ?? RecordingState.Idle;

    private async Task StartRecording()
    {
        if (_recordingService == null || _mainVm == null) return;

        // Check if FFmpeg is available
        if (!_mainVm.FfmpegDownloader.IsFFmpegAvailable())
        {
            if (!_mainVm.FfmpegDownloader.IsDownloading)
            {
                // Trigger download if not started
                _ = _mainVm.FfmpegDownloader.EnsureFFmpegAsync();
            }
            
            _mainVm.SetStatus("FFmpegNotReady");
            return;
        }
        
        string format = _mainVm.RecordFormat?.ToLowerInvariant() ?? "mp4";

        // Use TempFolder setting if available, otherwise local Temp folder in app directory
        string tempDir = _mainVm.TempDirectory;
        if (string.IsNullOrEmpty(tempDir))
        {
            tempDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Temp");
        }
        
        try { System.IO.Directory.CreateDirectory(tempDir); } catch { }

        if (_mainVm.UseFixedRecordPath && !string.IsNullOrEmpty(_mainVm.VideoSaveDirectory))
        {
             // Ensure directory exists
             try { System.IO.Directory.CreateDirectory(_mainVm.VideoSaveDirectory); } catch { }
             string fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.{format}";
             _currentRecordingPath = System.IO.Path.Combine(_mainVm.VideoSaveDirectory, fileName);
        }
        else
        {
             _currentRecordingPath = System.IO.Path.Combine(tempDir, $"GimmeCapture_{Guid.NewGuid()}.{format}");
        }
        
        var region = SelectionRect;
        
        // Ensure size is even for ffmpeg
        if (region.Width % 2 != 0) region = region.WithWidth(region.Width - 1);
        if (region.Height % 2 != 0) region = region.WithHeight(region.Height - 1);

        if (await _recordingService.StartAsync(region, _currentRecordingPath, format, _mainVm.ShowRecordCursor, ScreenOffset, VisualScaling))
        {
            RecordingDuration = TimeSpan.Zero;
            
            _recordTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _recordTimer.Tick += (s, e) => {
                if (RecState == RecordingState.Recording)
                    RecordingDuration = RecordingDuration.Add(TimeSpan.FromSeconds(1));
            };
            _recordTimer.Start();
        }
    }

    private async Task PauseRecording()
    {
        if (_recordingService == null) return;
        if (RecState == RecordingState.Recording) await _recordingService.PauseAsync();
        else if (RecState == RecordingState.Paused) await _recordingService.ResumeAsync();
    }

    private async Task StopRecording()
    {
        if (_recordingService == null || _mainVm == null) return;
        
        _recordTimer?.Stop();
        await _recordingService.StopAsync();

        // Use the actual output path from RecordingService (may have been modified during finalization)
        string? actualOutputPath = _recordingService.OutputFilePath ?? _currentRecordingPath;

        // Check if we need to prompt
        if (!_mainVm.UseFixedRecordPath && PickSaveFileAction != null && !string.IsNullOrEmpty(actualOutputPath))
        {
            if (System.IO.File.Exists(actualOutputPath))
            {
                var targetPath = await PickSaveFileAction();
                if (!string.IsNullOrEmpty(targetPath))
                {
                    try
                    {
                        if (System.IO.File.Exists(targetPath)) System.IO.File.Delete(targetPath);
                        System.IO.File.Move(actualOutputPath!, targetPath);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to move recording: {ex.Message}");
                    }
                }
                else
                {
                    // User cancelled, delete temp file
                    try
                    {
                        if (System.IO.File.Exists(actualOutputPath))
                        {
                            System.IO.File.Delete(actualOutputPath);
                            System.Diagnostics.Debug.WriteLine($"Deleted cancelled recording: {actualOutputPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to delete cancelled recording: {ex.Message}");
                    }
                }
            }
        }

        CloseAction?.Invoke();
    }

    private bool _isProcessingRecording = false;

    private async Task CopyRecording()
    {
        if (_isProcessingRecording || _recordingService == null || _mainVm == null) return;
        
        _isProcessingRecording = true;
        try
        {
            _recordTimer?.Stop();
            await _recordingService.StopAsync();

            // RecordingService.StopAsync finishes internal tasks but file system might be lagging slightly
            // Or the path might miss extension because RecordingService handles it internally
            
            string? actualOutputPath = _recordingService.OutputFilePath ?? _currentRecordingPath;
            
            // Correction: RecordingService might add .mkv extension if missing but not update the property?
            // Let's check typical variants
            if (!string.IsNullOrEmpty(actualOutputPath) && !System.IO.File.Exists(actualOutputPath))
            {
               if (!actualOutputPath.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
               {
                   string withExt = actualOutputPath + ".mkv";
                   if (System.IO.File.Exists(withExt)) actualOutputPath = withExt;
               }
            }

            // Wait loop for existence (up to 2 seconds)
            if (!string.IsNullOrEmpty(actualOutputPath))
            {
                for (int i = 0; i < 20; i++) 
                {
                    if (System.IO.File.Exists(actualOutputPath)) break;
                    await Task.Delay(100);
                }
            }
            
            if (!string.IsNullOrEmpty(actualOutputPath) && System.IO.File.Exists(actualOutputPath))
            {
                try
                {
                    // Use PowerShell as the ultimate fallback for clipboard operations
                    // This bypasses all .NET threading/apartment/Avalonia issues
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = $"-noprofile -command \"Set-Clipboard -Path '{actualOutputPath}'\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    var process = System.Diagnostics.Process.Start(psi);
                    process?.WaitForExit(2000); // Wait up to 2 seconds
                    
                    // Success, no message required as per user request
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to copy recording to clipboard: {ex.Message}");
                }
            }
            else 
            {
                 // Debugging help: log error
                 System.Diagnostics.Debug.WriteLine($"Video file not found at: {actualOutputPath}");
            }

            CloseAction?.Invoke();
        }
        finally
        {
            _isProcessingRecording = false;
        }
    }

    private async Task PinRecording()
    {
        if (_isProcessingRecording || _recordingService == null) return;
        
        // Capture state BEFORE stopping, because StopAsync sets it to Idle
        bool wasRecording = _recordingService.State == RecordingState.Recording;

        _isProcessingRecording = true;
        try
        {
            _recordTimer?.Stop();
            await _recordingService.StopAsync();
            
            // ... (screenshot logic omitted for brevity if irrelevant, keeping existing structure)
            
            // Start playing the video with ffplay (frameless, on top)
            if (wasRecording) // Use captured state
            {
                  // Stop already called above
                  
                  var recordingPath = _recordingService.LastRecordingPath;
                  if (string.IsNullOrEmpty(recordingPath) || !System.IO.File.Exists(recordingPath)) 
                  {
                      System.Diagnostics.Debug.WriteLine($"找不到錄影檔案: {recordingPath}");
                      return;
                  }

                 // Use path from service (checks system path too)
                 var ffplayPath = _recordingService.Downloader.GetFFplayPath();
                 
                 // Fallback check
                  if (string.IsNullOrEmpty(ffplayPath) || !System.IO.File.Exists(ffplayPath))
                  {
                      System.Diagnostics.Debug.WriteLine($"找不到播放器組件 (ffplay.exe)");
                    _isProcessingRecording = false;
                      return;
                  }

                 // Calculate Geometry
                 // ffplay expects physical screen coordinates for window position
                 double scaling = VisualScaling;
                 int x = (int)(SelectionRect.X * scaling) + ScreenOffset.X;
                 int y = (int)(SelectionRect.Y * scaling) + ScreenOffset.Y;
                 
                 // w and h should be physical pixels for capture, but stored in VM alongside logical original size
                 int w = (int)(SelectionRect.Width * scaling);
                 int h = (int)(SelectionRect.Height * scaling);
                 double logW = SelectionRect.Width;
                 double logH = SelectionRect.Height;
                 
                 // Create and show FloatingVideoWindow instead of raw ffplay
                 Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                 {
                     var videoVm = new FloatingVideoViewModel(
                         recordingPath, 
                         ffplayPath.Replace("ffplay.exe", "ffmpeg.exe"), // We need ffmpeg for streaming
                         w, h, 
                         logW, logH,
                         SelectionBorderColor, 
                         SelectionBorderThickness,
                         _mainVm?.HideRecordPinDecoration ?? false,
                         _mainVm?.HideRecordPinBorder ?? false);
                         
                     var videoWin = new FloatingVideoWindow
                     {
                         DataContext = videoVm,
                         Width = logW + 20, // Add margin from XAML (logical)
                         Height = logH + 20,
                         Position = new PixelPoint(x - (int)(10 * scaling), y - (int)(10 * scaling))
                     };
                     
                     videoWin.Show();
                 });
                 
                 CloseAction?.Invoke();
            }
        }
        finally
        {
            _isProcessingRecording = false;
        }
    }

    private string? _currentRecordingPath;

    public static class StaticData
    {
        public static Color[] ColorsList { get; } = new[]
        {
            Colors.Red, Colors.Green, Colors.Blue, 
            Colors.Yellow, Colors.Cyan, Colors.Magenta,
            Colors.White, Colors.Black, Colors.Gray
        };
    }

    private async Task Copy() 
    { 
        // If recording is processing, ignore copy command to prevent overwriting with screenshot
        if (_isProcessingRecording) return;

        // If recording is active, copy recording instead of screenshot
        if (RecState == RecordingState.Recording || RecState == RecordingState.Paused)
        {
            await CopyRecording();
            return;
        }

        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            HideAction?.Invoke();
            await Task.Delay(200); // Wait for UI update

            try 
            {
                IsProcessing = true;
                ProcessingText = LocalizationService.Instance["StatusProcessing"] ?? "Processing...";
                var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, ScreenOffset, VisualScaling, Annotations, _mainVm?.ShowSnipCursor ?? false);
                await _captureService.CopyToClipboardAsync(bitmap);
                _mainVm?.SetStatus("StatusCopied");
            }
            finally
            {
                IsProcessing = false;
                CloseAction?.Invoke();
            }
        }
    }

    private async Task Save() 
    { 
         // If recording is active, stop recording instead of saving screenshot
         if (RecState == RecordingState.Recording || RecState == RecordingState.Paused)
         {
             await StopRecording();
             return;
         }

         if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
         {
             HideAction?.Invoke();
             await Task.Delay(200); // Wait for UI update

             try
             {
                 IsProcessing = true;
                 ProcessingText = LocalizationService.Instance["StatusSaving"] ?? "Saving...";
                 var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, ScreenOffset, VisualScaling, Annotations, _mainVm?.ShowSnipCursor ?? false);
                 
                 if (_mainVm != null && _mainVm.AutoSave)
                 {
                     var dir = _mainVm.SaveDirectory;
                     if (string.IsNullOrEmpty(dir))
                     {
                         dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), "GimmeCapture");
                     }
                     try { System.IO.Directory.CreateDirectory(dir); } catch { }

                     var fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                     var path = System.IO.Path.Combine(dir, fileName);
                     await _captureService.SaveToFileAsync(bitmap, path);
                     _mainVm?.SetStatus("StatusSaved");
                     System.Diagnostics.Debug.WriteLine($"Auto-saved to {path}");
                 }
                 else if (PickSaveFileAction != null)
                 {
                     var path = await PickSaveFileAction.Invoke();
                     if (!string.IsNullOrEmpty(path))
                     {
                        await _captureService.SaveToFileAsync(bitmap, path);
                        _mainVm?.SetStatus("StatusSaved");
                     }
                     System.Diagnostics.Debug.WriteLine($"Saved to {path}");
                 }
                 else
                 {
                     // Fallback
                     var fileName = $"Capture_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                     var path = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures), fileName);
                     await _captureService.SaveToFileAsync(bitmap, path);
                 }
             }
             finally
             {
                 IsProcessing = false;
                 CloseAction?.Invoke(); 
             }
         }
    }
    
    private async Task Pin(bool runAI = false)
    {
        // If recording is active, pin recording instead of screenshot
        if (RecState == RecordingState.Recording || RecState == RecordingState.Paused)
        {
            await PinRecording();
            return;
        }

        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            HideAction?.Invoke();
            await Task.Delay(200); // Wait for UI update
            
            try
            {
                var skBitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, ScreenOffset, VisualScaling, Annotations, _mainVm?.ShowSnipCursor ?? false);
                
                // Convert SKBitmap to Avalonia Bitmap
                using var image = SkiaSharp.SKImage.FromBitmap(skBitmap);
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var stream = new System.IO.MemoryStream();
                data.SaveTo(stream);
                stream.Position = 0;
                
                var avaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                
                // Open Floating Window
            OpenPinWindowAction?.Invoke(avaloniaBitmap, SelectionRect, SelectionBorderColor, SelectionBorderThickness, runAI);
            }
            finally
            {
                CloseAction?.Invoke();
            }
        }
    }

    private void Close() { CloseAction?.Invoke(); }
    
    public Action? CloseAction { get; set; }
    public Action? HideAction { get; set; }
    public Func<Task<string?>>? PickSaveFileAction { get; set; }
    public System.Action<Avalonia.Media.Imaging.Bitmap, Rect, Color, double, bool>? OpenPinWindowAction { get; set; }
}
