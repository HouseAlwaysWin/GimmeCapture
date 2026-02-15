using Avalonia;
using Avalonia.Media;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using GimmeCapture.Models;

// using GimmeCapture.ViewModels.Main; // Already imported or namespace match? 
// Check namespaces in original file. 
using GimmeCapture.Services.Core;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using System.Linq;

using System.Reactive.Linq;

namespace GimmeCapture.ViewModels.Floating;

public abstract class FloatingWindowViewModelBase : ViewModelBase, IDisposable
{
    // Actions / Delegates (View Callbacks)
    public System.Action? CloseAction { get; set; }
    public System.Func<Task>? SaveAction { get; set; }
    public Action<Avalonia.PixelPoint, double, double, double, double>? RequestSetWindowRect { get; set; }
    public System.Action? FocusWindowAction { get; set; }

    // Selection
    private Avalonia.Rect _selectionRect = new Avalonia.Rect();
    public Avalonia.Rect SelectionRect
    {
        get => _selectionRect;
        set 
        {
            this.RaiseAndSetIfChanged(ref _selectionRect, value);
            this.RaisePropertyChanged(nameof(IsSelectionActive));
        }
    }
    public bool IsSelectionActive => SelectionRect.Width > 0 && SelectionRect.Height > 0;
    // Border
    private Color _borderColor = Colors.Red;
    public Color BorderColor
    {
        get => _borderColor;
        set => this.RaiseAndSetIfChanged(ref _borderColor, value);
    }

    private double _borderThickness = 2.0;
    public double BorderThickness
    {
        get => _borderThickness;
        set => this.RaiseAndSetIfChanged(ref _borderThickness, value);
    }

    private bool _hidePinDecoration = false;
    public bool HidePinDecoration
    {
        get => _hidePinDecoration;
        set
        {
            this.RaiseAndSetIfChanged(ref _hidePinDecoration, value);
            this.RaisePropertyChanged(nameof(WindowPadding));
        }
    }

    private bool _hidePinBorder = false;
    public bool HidePinBorder
    {
        get => _hidePinBorder;
        set => this.RaiseAndSetIfChanged(ref _hidePinBorder, value);
    }

    // Dimensions
    private double _originalWidth;
    public double OriginalWidth
    {
        get => _originalWidth;
        set => this.RaiseAndSetIfChanged(ref _originalWidth, value);
    }

    private double _originalHeight;
    public double OriginalHeight
    {
        get => _originalHeight;
        set => this.RaiseAndSetIfChanged(ref _originalHeight, value);
    }

    private double _displayWidth;
    public double DisplayWidth
    {
        get => _displayWidth;
        set => this.RaiseAndSetIfChanged(ref _displayWidth, value);
    }

    private double _displayHeight;
    public double DisplayHeight
    {
        get => _displayHeight;
        set => this.RaiseAndSetIfChanged(ref _displayHeight, value);
    }

    // Decoration Scale
    private double _wingScale = 1.0;
    public double WingScale
    {
        get => _wingScale;
        set
        {
            this.RaiseAndSetIfChanged(ref _wingScale, value);
            this.RaisePropertyChanged(nameof(WingWidth));
            this.RaisePropertyChanged(nameof(WingHeight));
            this.RaisePropertyChanged(nameof(LeftWingMargin));
            this.RaisePropertyChanged(nameof(RightWingMargin));
            this.RaisePropertyChanged(nameof(WindowPadding));
        }
    }

    private double _cornerIconScale = 1.0;
    public double CornerIconScale
    {
        get => _cornerIconScale;
        set
        {
            this.RaiseAndSetIfChanged(ref _cornerIconScale, value);
            this.RaisePropertyChanged(nameof(SelectionIconSize));
        }
    }

    // Derived Decoration Props
    public double WingWidth => 100 * WingScale;
    public double WingHeight => 60 * WingScale;
    public double SelectionIconSize => 22 * CornerIconScale;
    public Thickness LeftWingMargin => new Thickness(-WingWidth, 0, 0, 0);
    public Thickness RightWingMargin => new Thickness(0, 0, -WingWidth, 0);

    // Padding
    public abstract Thickness WindowPadding { get; }

    // Toolbar Properties
    private bool _showToolbar = true;
    public bool ShowToolbar
    {
        get => _showToolbar;
        set 
        {
            this.RaiseAndSetIfChanged(ref _showToolbar, value);
            this.RaisePropertyChanged(nameof(WindowPadding));
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

    private double _toolbarWidth;
    public double ToolbarWidth
    {
        get => _toolbarWidth;
        set 
        {
            this.RaiseAndSetIfChanged(ref _toolbarWidth, value);
            UpdateToolbarPosition();
        }
    }

    private double _toolbarHeight;
    public double ToolbarHeight
    {
        get => _toolbarHeight;
        set 
        {
            this.RaiseAndSetIfChanged(ref _toolbarHeight, value);
            UpdateToolbarPosition();
        }
    }

    private Thickness _toolbarMargin = new Thickness(0, 0, 0, 10);
    public Thickness ToolbarMargin
    {
        get => _toolbarMargin;
        set => this.RaiseAndSetIfChanged(ref _toolbarMargin, value);
    }

    private bool _isToolbarFlipped;
    public bool IsToolbarFlipped
    {
        get => _isToolbarFlipped;
        set => this.RaiseAndSetIfChanged(ref _isToolbarFlipped, value);
    }

    // Tools & Annotations
    private FloatingTool _currentTool = FloatingTool.None;
    public virtual FloatingTool CurrentTool
    {
        get => _currentTool;
        set 
        {
            this.RaiseAndSetIfChanged(ref _currentTool, value);
            this.RaisePropertyChanged(nameof(IsSelectionMode));
            this.RaisePropertyChanged(nameof(IsAnyToolActive));
        }
    }
    
    private AnnotationType _currentAnnotationTool = AnnotationType.None;
    public virtual AnnotationType CurrentAnnotationTool
    {
        get => _currentAnnotationTool;
        set 
        {
            this.RaiseAndSetIfChanged(ref _currentAnnotationTool, value);
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsTextToolActive));
            this.RaisePropertyChanged(nameof(IsPenToolActive));
            this.RaisePropertyChanged(nameof(IsAnyToolActive));
        }
    }

    // Helper Properties
    public bool IsSelectionMode
    {
        get => CurrentTool == FloatingTool.Selection;
        set => CurrentTool = value ? FloatingTool.Selection : (CurrentTool == FloatingTool.Selection ? FloatingTool.None : CurrentTool);
    }
    public bool IsAnyToolActive => CurrentTool != FloatingTool.None || CurrentAnnotationTool != AnnotationType.None;
    
    public bool IsShapeToolActive => CurrentAnnotationTool == AnnotationType.Rectangle || CurrentAnnotationTool == AnnotationType.Ellipse || CurrentAnnotationTool == AnnotationType.Arrow || CurrentAnnotationTool == AnnotationType.Line || CurrentAnnotationTool == AnnotationType.Mosaic || CurrentAnnotationTool == AnnotationType.Blur;
    public bool IsPenToolActive => CurrentAnnotationTool == AnnotationType.Pen;
    public bool IsTextToolActive => CurrentAnnotationTool == AnnotationType.Text;

    public ObservableCollection<Annotation> Annotations { get; } = new();

    private Avalonia.Media.Color _selectedColor = Avalonia.Media.Colors.Red;
    public Avalonia.Media.Color SelectedColor
    {
        get => _selectedColor;
        set => this.RaiseAndSetIfChanged(ref _selectedColor, value);
    }

    private double _currentThickness = 4.0;
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

    // Text Tool State
    private bool _isEnteringText;
    public bool IsEnteringText
    {
        get => _isEnteringText;
        set => this.RaiseAndSetIfChanged(ref _isEnteringText, value);
    }

    private string _pendingText = string.Empty;
    public string PendingText
    {
        get => _pendingText;
        set => this.RaiseAndSetIfChanged(ref _pendingText, value);
    }

    private Avalonia.Point _textInputPosition;
    public Avalonia.Point TextInputPosition
    {
        get => _textInputPosition;
        set => this.RaiseAndSetIfChanged(ref _textInputPosition, value);
    }

    private Avalonia.Media.FontFamily _currentFontFamily = new Avalonia.Media.FontFamily("Arial");
    public Avalonia.Media.FontFamily CurrentFontFamily
    {
        get => _currentFontFamily;
        set => this.RaiseAndSetIfChanged(ref _currentFontFamily, value);
    }

    public ObservableCollection<double> Thicknesses { get; } = new() { 1, 2, 4, 6, 8, 12, 16, 24 };

    public ObservableCollection<Avalonia.Media.FontFamily> AvailableFonts { get; } = new ObservableCollection<Avalonia.Media.FontFamily>
    {
        new Avalonia.Media.FontFamily("Arial"), 
        new Avalonia.Media.FontFamily("Segoe UI"), 
        new Avalonia.Media.FontFamily("Consolas"), 
        new Avalonia.Media.FontFamily("Times New Roman"), 
        new Avalonia.Media.FontFamily("Comic Sans MS"), 
        new Avalonia.Media.FontFamily("Microsoft JhengHei"), 
        new Avalonia.Media.FontFamily("Meiryo")
    };

    // Shared preset colors from Main ViewModel - accessing static data?
    // Referencing SnipWindowViewModel directly might be circular if not careful, but static data is fine.
    public System.Collections.Generic.IEnumerable<Avalonia.Media.Color> PresetColors => GimmeCapture.ViewModels.Main.SnipWindowViewModel.StaticData.ColorsList;

    // Commands
    // Commands
    public ReactiveCommand<Unit, Unit> ToggleToolbarCommand { get; protected set; } = null!;
    public ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> ConfirmTextEntryCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> CancelTextEntryCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearAnnotationsCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> UndoCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> RedoCommand { get; protected set; } = null!;
    public ReactiveCommand<string, Unit> ToggleToolGroupCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> SelectionCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> CloseCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> SaveCommand { get; protected set; } = null!;
    
    // Annotation Commands
    public ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseFontSizeCommand { get; protected set; } = null!;
    public ReactiveCommand<Avalonia.Media.Color, Unit> ChangeColorCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; protected set; } = null!;

    // History
    protected Stack<IHistoryAction> _historyStack = new();
    protected Stack<IHistoryAction> _redoHistoryStack = new();
    
    private bool _hasUndo;
    public bool HasUndo
    {
        get => _hasUndo;
        protected set => this.RaiseAndSetIfChanged(ref _hasUndo, value);
    }

    private bool _hasRedo;
    public bool HasRedo
    {
        get => _hasRedo;
        protected set => this.RaiseAndSetIfChanged(ref _hasRedo, value);
    }

    // Processing State
    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set 
        {
            this.RaiseAndSetIfChanged(ref _isProcessing, value);
            this.RaisePropertyChanged(nameof(ShowProcessingOverlay));
        }
    }

    public bool ShowProcessingOverlay => IsProcessing;

    private string _processingText = LocalizationService.Instance["StatusProcessing"];
    public string ProcessingText
    {
        get => _processingText;
        set => this.RaiseAndSetIfChanged(ref _processingText, value);
    }

    private string _diagnosticText = "Ready";
    public string DiagnosticText
    {
        get => _diagnosticText;
        set => this.RaiseAndSetIfChanged(ref _diagnosticText, value);
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set => this.RaiseAndSetIfChanged(ref _progressValue, value);
    }
    
    private bool _isIndeterminate = true;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => this.RaiseAndSetIfChanged(ref _isIndeterminate, value);
    }

    // Position
    private PixelPoint? _screenPosition;
    public PixelPoint? ScreenPosition
    {
        get => _screenPosition;
        set 
        {
            this.RaiseAndSetIfChanged(ref _screenPosition, value);
            UpdateToolbarPosition();
        }
    }

    // Hotkeys (Virtual to allow overriding or service-based values)
    public virtual string CopyHotkey => "Ctrl+C";
    public virtual string PinHotkey => "F3";
    public virtual string UndoHotkey => "Ctrl+Z";
    public virtual string RedoHotkey => "Ctrl+Y";
    public virtual string ClearHotkey => "Delete";
    public virtual string SaveHotkey => "Ctrl+S";
    public virtual string CloseHotkey => "Escape";
    
    public virtual string RectangleHotkey => "R";
    public virtual string EllipseHotkey => "E";
    public virtual string ArrowHotkey => "A";
    public virtual string LineHotkey => "L";
    public virtual string PenHotkey => "P";
    public virtual string TextHotkey => "T";
    public virtual string MosaicHotkey => "M";
    public virtual string BlurHotkey => "B";

    // Scale Commands
    public ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; protected set; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; protected set; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; protected set; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; protected set; } = ReactiveCommand.Create(() => {});

    public virtual void UpdateToolbarPosition() { }

    protected void InitializeBaseCommands()
    {
        CloseCommand = ReactiveCommand.Create(() => 
        {
            Dispose();
            CloseAction?.Invoke();
        });

        SaveCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (SaveAction != null) await SaveAction();
        });

        ToggleToolbarCommand = ReactiveCommand.Create(() => { ShowToolbar = !ShowToolbar; });

        SelectionCommand = ReactiveCommand.Create(() => 
        {
            CurrentTool = CurrentTool == FloatingTool.Selection ? FloatingTool.None : FloatingTool.Selection;
        });

        SelectToolCommand = ReactiveCommand.Create<AnnotationType>(tool => 
        {
            var targetTool = CurrentAnnotationTool == tool ? AnnotationType.None : tool;
            if (targetTool != AnnotationType.None)
            {
                CurrentTool = FloatingTool.None;
            }
            CurrentAnnotationTool = targetTool;
        });

        ToggleToolGroupCommand = ReactiveCommand.Create<string>(group => 
        {
             AnnotationType targetTool = AnnotationType.None;
             if (group == "Shapes")
             {
                 targetTool = IsShapeToolActive ? AnnotationType.None : AnnotationType.Rectangle;
             }
             else if (group == "Pen")
             {
                 targetTool = (CurrentAnnotationTool == AnnotationType.Pen) ? AnnotationType.None : AnnotationType.Pen;
             }
             else if (group == "Text")
             {
                 targetTool = IsTextToolActive ? AnnotationType.None : AnnotationType.Text;
             }

             if (targetTool != AnnotationType.None)
             {
                 CurrentTool = FloatingTool.None;
             }
             CurrentAnnotationTool = targetTool;
        });
    }

    protected void InitializeAnnotationCommands()
    {
        IncreaseFontSizeCommand = ReactiveCommand.Create(() => { CurrentFontSize = Math.Min(CurrentFontSize + 2, 72); });
        DecreaseFontSizeCommand = ReactiveCommand.Create(() => { CurrentFontSize = Math.Max(CurrentFontSize - 2, 8); });
        ChangeColorCommand = ReactiveCommand.Create<Avalonia.Media.Color>(c => SelectedColor = c);
        IncreaseThicknessCommand = ReactiveCommand.Create(() => { CurrentThickness = Math.Min(CurrentThickness + 1, 30); });
        DecreaseThicknessCommand = ReactiveCommand.Create(() => { CurrentThickness = Math.Max(CurrentThickness - 1, 1); });
        
        ClearAnnotationsCommand = ReactiveCommand.Create(ClearAnnotations);

        var canUndo = this.WhenAnyValue(x => x.HasUndo).ObserveOn(RxApp.MainThreadScheduler);
        UndoCommand = ReactiveCommand.Create(Undo, canUndo);

        var canRedo = this.WhenAnyValue(x => x.HasRedo).ObserveOn(RxApp.MainThreadScheduler);
        RedoCommand = ReactiveCommand.Create(Redo, canRedo);

        ConfirmTextEntryCommand = ReactiveCommand.Create(() => 
        {
            if (!string.IsNullOrWhiteSpace(PendingText))
            {
                AddAnnotation(new Annotation
                {
                    Type = AnnotationType.Text,
                    StartPoint = TextInputPosition,
                    EndPoint = TextInputPosition,
                    Text = PendingText,
                    Color = SelectedColor,
                    FontSize = CurrentFontSize,
                    FontFamily = CurrentFontFamily,
                    IsBold = IsBold,
                    IsItalic = IsItalic
                });
            }
            IsEnteringText = false;
            PendingText = string.Empty;
            FocusWindowAction?.Invoke();
        });

        CancelTextEntryCommand = ReactiveCommand.Create(() => 
        {
            IsEnteringText = false;
            PendingText = string.Empty;
            FocusWindowAction?.Invoke();
        });
    }
    
    public void AddAnnotation(Annotation annotation)
    {
        Annotations.Add(annotation);
        PushUndoAction(new AnnotationHistoryAction(Annotations, annotation, true));
    }

    public void ClearAnnotations()
    {
        if (Annotations.Count == 0) return;
        PushUndoAction(new ClearAnnotationsHistoryAction(Annotations));
        Annotations.Clear();
    }

    public void PushUndoAction(IHistoryAction action)
    {
        _historyStack.Push(action);
        _redoHistoryStack.Clear();
        UpdateHistoryStatus();
    }

    public void PushResizeAction(Avalonia.PixelPoint oldPos, double oldW, double oldH, double oldContentW, double oldContentH,
                                Avalonia.PixelPoint newPos, double newW, double newH, double newContentW, double newContentH)
    {
        if (oldPos == newPos && oldW == newW && oldH == newH && oldContentW == newContentW && oldContentH == newContentH) return;
        
        PushUndoAction(new WindowTransformHistoryAction(
            (pos, w, h, cw, ch) => {
                DisplayWidth = cw;
                DisplayHeight = ch;
                RequestSetWindowRect?.Invoke(pos, w, h, cw, ch);
            },
            oldPos, oldW, oldH, oldContentW, oldContentH,
            newPos, newW, newH, newContentW, newContentH));
    }

    protected virtual void Undo()
    {
        if (_historyStack.Count == 0) return;
        var action = _historyStack.Pop();
        action.Undo();
        _redoHistoryStack.Push(action);
        UpdateHistoryStatus();
    }

    protected virtual void Redo()
    {
        if (_redoHistoryStack.Count == 0) return;
        var action = _redoHistoryStack.Pop();
        action.Redo();
        _historyStack.Push(action);
        UpdateHistoryStatus();
    }

    protected void UpdateHistoryStatus()
    {
        HasUndo = _historyStack.Count > 0;
        HasRedo = _redoHistoryStack.Count > 0;
    }

    public virtual void Dispose() {} 
}
