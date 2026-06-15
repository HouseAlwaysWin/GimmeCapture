using Avalonia;
using Avalonia.Media;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Shared;

using GimmeCapture.Services.Core;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using System.Reactive.Linq;

namespace GimmeCapture.ViewModels.Floating;

public abstract class FloatingWindowViewModelBase : ViewModelBase, IDisposable
{
    private readonly AnnotationEditorState _editorState = new();
    protected AnnotationEditorState EditorState => _editorState;
    private readonly IDisposable _editorStateSubscription;

    protected FloatingWindowViewModelBase()
    {
        _editorStateSubscription = _editorState.Changed.Subscribe(_ =>
        {
            this.RaisePropertyChanged(nameof(CurrentAnnotationTool));
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsPenToolActive));
            this.RaisePropertyChanged(nameof(IsTextToolActive));
            this.RaisePropertyChanged(nameof(IsRedactionToolActive));
            this.RaisePropertyChanged(nameof(IsAnyToolActive));
            this.RaisePropertyChanged(nameof(SelectedAnnotation));
            this.RaisePropertyChanged(nameof(StyleAnnotationTool));
            this.RaisePropertyChanged(nameof(SelectedColor));
            this.RaisePropertyChanged(nameof(CurrentThickness));
            this.RaisePropertyChanged(nameof(CurrentFontSize));
            this.RaisePropertyChanged(nameof(CurrentFontFamily));
            this.RaisePropertyChanged(nameof(IsBold));
            this.RaisePropertyChanged(nameof(IsItalic));
            this.RaisePropertyChanged(nameof(IsEnteringText));
            this.RaisePropertyChanged(nameof(PendingText));
            this.RaisePropertyChanged(nameof(TextInputPosition));
            this.RaisePropertyChanged(nameof(HasUndo));
            this.RaisePropertyChanged(nameof(HasRedo));
            this.RaisePropertyChanged(nameof(CurrentRedactionPreset));
            UpdateHistoryStatus();
        });
    }

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
        set
        {
            this.RaiseAndSetIfChanged(ref _borderThickness, value);
            this.RaisePropertyChanged(nameof(SelectionThickness));
            this.RaisePropertyChanged(nameof(SelectionBorderOuterMargin));
        }
    }

    public double SelectionThickness => BorderThickness;

    public Thickness SelectionBorderOuterMargin => new(-SelectionThickness, -SelectionThickness, -SelectionThickness, -SelectionThickness);

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
    public double SelectionIconSize => 8;
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
    
    public virtual AnnotationType CurrentAnnotationTool
    {
        get => _editorState.CurrentAnnotationTool;
        set => _editorState.CurrentAnnotationTool = value;
    }

    // Helper Properties
    public bool IsSelectionMode
    {
        get => CurrentTool == FloatingTool.Selection;
        set => CurrentTool = value ? FloatingTool.Selection : (CurrentTool == FloatingTool.Selection ? FloatingTool.None : CurrentTool);
    }
    public virtual bool IsAnyToolActive => CurrentTool != FloatingTool.None || CurrentAnnotationTool != AnnotationType.None;
    
    public bool IsShapeToolActive => _editorState.IsShapeToolActive;
    public bool IsPenToolActive => _editorState.IsPenToolActive;
    public bool IsTextToolActive => _editorState.IsTextToolActive;
    public bool IsRedactionToolActive => _editorState.IsRedactionToolActive;

    public ObservableCollection<Annotation> Annotations => _editorState.Annotations;
    public Annotation? SelectedAnnotation => _editorState.SelectedAnnotation;
    public AnnotationType StyleAnnotationTool => _editorState.StyleAnnotationTool;

    public Avalonia.Media.Color SelectedColor
    {
        get => _editorState.SelectedColor;
        set => _editorState.SelectedColor = value;
    }

    public double CurrentThickness
    {
        get => _editorState.CurrentThickness;
        set => _editorState.CurrentThickness = value;
    }

    public double CurrentFontSize
    {
        get => _editorState.CurrentFontSize;
        set => _editorState.CurrentFontSize = value;
    }
    
    public bool IsBold
    {
        get => _editorState.IsBold;
        set => _editorState.IsBold = value;
    }

    public bool IsItalic
    {
        get => _editorState.IsItalic;
        set => _editorState.IsItalic = value;
    }

    // Text Tool State
    public bool IsEnteringText
    {
        get => _editorState.IsEnteringText;
        set => _editorState.IsEnteringText = value;
    }

    public string PendingText
    {
        get => _editorState.PendingText;
        set => _editorState.PendingText = value;
    }

    public Avalonia.Point TextInputPosition
    {
        get => _editorState.TextInputPosition;
        set => _editorState.TextInputPosition = value;
    }

    public Avalonia.Media.FontFamily CurrentFontFamily
    {
        get => _editorState.CurrentFontFamily;
        set => _editorState.CurrentFontFamily = value;
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
    public System.Collections.Generic.IEnumerable<Avalonia.Media.Color> PresetColors => PresetColorPalette.DefaultColors;

    // Commands
    // Commands
    public ReactiveCommand<Unit, Unit> ToggleToolbarCommand { get; protected set; } = null!;
    public ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> ConfirmTextEntryCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> CancelTextEntryCommand { get; protected set; } = null!;
    public ReactiveCommand<Unit, Unit> DeleteSelectedAnnotationCommand { get; protected set; } = null!;
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
    public ReactiveCommand<string, Unit> SetRedactionPresetCommand { get; protected set; } = null!;

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

    public string CurrentRedactionPreset => _editorState.CurrentRedactionPreset;

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
    public virtual string PinHotkey => "F6";
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
    public virtual string SelectionHotkey => "S";
    public virtual string CropHotkey => "C";

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

        var notEnteringText = this.WhenAnyValue(x => x.IsEnteringText).Select(x => !x).ObserveOn(RxApp.MainThreadScheduler);

        ToggleToolbarCommand = ReactiveCommand.Create(() => { ShowToolbar = !ShowToolbar; }, notEnteringText);

        SelectionCommand = ReactiveCommand.Create(() => 
        {
            CurrentTool = CurrentTool == FloatingTool.Selection ? FloatingTool.None : FloatingTool.Selection;
        }, notEnteringText);

        SelectToolCommand = ReactiveCommand.Create<AnnotationType>(tool => 
        {
            var targetTool = CurrentAnnotationTool == tool ? AnnotationType.None : tool;
            if (targetTool != AnnotationType.None)
            {
                CurrentTool = FloatingTool.None;
            }
            CurrentAnnotationTool = targetTool;
        }, notEnteringText);

        ToggleToolGroupCommand = ReactiveCommand.Create<string>(group => 
        {
            var targetTool = _editorState.GetToolGroupTarget(group);
            if (targetTool != AnnotationType.None)
            {
                CurrentTool = FloatingTool.None;
            }
            CurrentAnnotationTool = targetTool;
        }, notEnteringText);
    }

    protected void InitializeAnnotationCommands()
    {
        var notEnteringText = this.WhenAnyValue(x => x.IsEnteringText).Select(x => !x).ObserveOn(RxApp.MainThreadScheduler);

        IncreaseFontSizeCommand = ReactiveCommand.Create(() => { CurrentFontSize = Math.Min(CurrentFontSize + 2, 72); });
        DecreaseFontSizeCommand = ReactiveCommand.Create(() => { CurrentFontSize = Math.Max(CurrentFontSize - 2, 8); });
        ChangeColorCommand = ReactiveCommand.Create<Avalonia.Media.Color>(_editorState.ApplySelectedColor);
        IncreaseThicknessCommand = ReactiveCommand.Create(() => _editorState.ApplySelectedThickness(Math.Min(CurrentThickness + 1, 30)));
        DecreaseThicknessCommand = ReactiveCommand.Create(() => _editorState.ApplySelectedThickness(Math.Max(CurrentThickness - 1, 1)));
        SetRedactionPresetCommand = ReactiveCommand.Create<string>(preset => _editorState.SetRedactionPreset(preset));

        var canDeleteSelected = this.WhenAnyValue(
            x => x.SelectedAnnotation,
            x => x.IsEnteringText,
            (annotation, enteringText) => annotation != null && !enteringText)
            .ObserveOn(RxApp.MainThreadScheduler);
        DeleteSelectedAnnotationCommand = ReactiveCommand.Create(
            () => { _editorState.RemoveSelectedAnnotation(); },
            canDeleteSelected);
        ClearAnnotationsCommand = ReactiveCommand.Create(ClearAnnotations, notEnteringText);

        var canUndo = this.WhenAnyValue(x => x.HasUndo, x => x.IsEnteringText, (u, t) => u && !t).ObserveOn(RxApp.MainThreadScheduler);
        UndoCommand = ReactiveCommand.Create(Undo, canUndo);

        var canRedo = this.WhenAnyValue(x => x.HasRedo, x => x.IsEnteringText, (r, t) => r && !t).ObserveOn(RxApp.MainThreadScheduler);
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
        _editorState.AddAnnotation(annotation);
    }

    public void BeginPendingAnnotation(Annotation annotation) => _editorState.BeginPendingAnnotation(annotation);

    public bool CommitPendingAnnotation(Annotation annotation) => _editorState.CommitPendingAnnotation(annotation);

    public void CancelPendingAnnotation(Annotation annotation) => _editorState.CancelPendingAnnotation(annotation);

    public void RemoveAnnotation(Annotation annotation) => _editorState.RemoveAnnotation(annotation);

    public void SelectAnnotation(Annotation? annotation) => _editorState.SelectAnnotation(annotation);

    public void ClearAnnotationSelection() => _editorState.ClearSelection();

    public AnnotationSnapshot BeginAnnotationEdit(Annotation annotation) => _editorState.BeginAnnotationEdit(annotation);

    public void CommitAnnotationEdit(Annotation annotation, AnnotationSnapshot before) =>
        _editorState.CommitAnnotationEdit(annotation, before);

    public Annotation CreateAnnotationForCurrentTool(Avalonia.Point startPoint, Bitmap? drawingModeSnapshot, Avalonia.Size drawingModeReferenceSize)
    {
        return _editorState.CreateAnnotationForCurrentTool(
            startPoint,
            drawingModeSnapshot,
            drawingModeReferenceSize);
    }

    public void ClearAnnotations()
    {
        _editorState.ClearAnnotations();
    }

    public void PushUndoAction(IHistoryAction action)
    {
        _editorState.PushUndoAction(action);
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
        _editorState.Undo();
    }

    protected virtual void Redo()
    {
        _editorState.Redo();
    }

    protected void UpdateHistoryStatus()
    {
        HasUndo = _editorState.HasUndo;
        HasRedo = _editorState.HasRedo;
    }

    public virtual void Dispose() 
    {
        _editorStateSubscription.Dispose();
        _editorState.Dispose();
    } 
}
