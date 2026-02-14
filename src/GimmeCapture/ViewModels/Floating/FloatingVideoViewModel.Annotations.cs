using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using System.Linq;
using System.Reactive.Linq;
using System;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel
{
    private AnnotationType _currentAnnotationTool = AnnotationType.None;
    public AnnotationType CurrentAnnotationTool
    {
        get => _currentAnnotationTool;
        set 
        {
            if (_currentAnnotationTool == value) return;
            
            if (value != AnnotationType.None)
            {
                CurrentTool = FloatingTool.None;
            }

            this.RaiseAndSetIfChanged(ref _currentAnnotationTool, value);
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsTextToolActive));
            this.RaisePropertyChanged(nameof(IsPenToolActive));
            this.RaisePropertyChanged(nameof(IsAnyToolActive));
        }
    }

    public ObservableCollection<Annotation> Annotations { get; } = new();

    public bool IsShapeToolActive => CurrentAnnotationTool == AnnotationType.Rectangle || CurrentAnnotationTool == AnnotationType.Ellipse || CurrentAnnotationTool == AnnotationType.Arrow || CurrentAnnotationTool == AnnotationType.Line || CurrentAnnotationTool == AnnotationType.Mosaic || CurrentAnnotationTool == AnnotationType.Blur;
    public bool IsPenToolActive => CurrentAnnotationTool == AnnotationType.Pen;
    public bool IsTextToolActive => CurrentAnnotationTool == AnnotationType.Text;

    private Avalonia.Media.Color _selectedColor = Avalonia.Media.Colors.Red;
    public Avalonia.Media.Color SelectedColor
    {
        get => _selectedColor;
        set => this.RaiseAndSetIfChanged(ref _selectedColor, value);
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

    private FontFamily _currentFontFamily = new FontFamily("Arial");
    public FontFamily CurrentFontFamily
    {
        get => _currentFontFamily;
        set => this.RaiseAndSetIfChanged(ref _currentFontFamily, value);
    }

    public ObservableCollection<double> Thicknesses { get; } = new() { 1, 2, 4, 6, 8, 12, 16, 24 };

    public ObservableCollection<FontFamily> AvailableFonts { get; } = new ObservableCollection<FontFamily>
    {
        new FontFamily("Arial"), 
        new FontFamily("Segoe UI"), 
        new FontFamily("Consolas"), 
        new FontFamily("Times New Roman"), 
        new FontFamily("Comic Sans MS"), 
        new FontFamily("Microsoft JhengHei"), 
        new FontFamily("Meiryo")
    };

    // Shared preset colors from Main ViewModel
    public System.Collections.Generic.IEnumerable<Avalonia.Media.Color> PresetColors => GimmeCapture.ViewModels.Main.SnipWindowViewModel.StaticData.ColorsList;

    public ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseFontSizeCommand { get; private set; } = null!;
    public ReactiveCommand<Avalonia.Media.Color, Unit> ChangeColorCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; private set; } = null!;

    public ReactiveCommand<Unit, Unit> ConfirmTextEntryCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CancelTextEntryCommand { get; private set; } = null!;
    
    public ReactiveCommand<Unit, Unit> ClearAnnotationsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> UndoCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RedoCommand { get; private set; } = null!;

    private Stack<IHistoryAction> _historyStack = new();
    private Stack<IHistoryAction> _redoHistoryStack = new();

    private bool _hasUndo;
    public bool HasUndo
    {
        get => _hasUndo;
        set => this.RaiseAndSetIfChanged(ref _hasUndo, value);
    }

    private bool _hasRedo;
    public bool HasRedo
    {
        get => _hasRedo;
        set => this.RaiseAndSetIfChanged(ref _hasRedo, value);
    }

    public bool CanUndo => HasUndo;
    public bool CanRedo => HasRedo;

    private void InitializeAnnotationCommands()
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

    private void ClearAnnotations()
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

    private void Undo()
    {
        if (_historyStack.Count == 0) return;
        var action = _historyStack.Pop();
        action.Undo();
        _redoHistoryStack.Push(action);
        UpdateHistoryStatus();
    }

    private void Redo()
    {
        if (_redoHistoryStack.Count == 0) return;
        var action = _redoHistoryStack.Pop();
        action.Redo();
        _historyStack.Push(action);
        UpdateHistoryStatus();
    }

    private void UpdateHistoryStatus()
    {
        HasUndo = _historyStack.Count > 0;
        HasRedo = _redoHistoryStack.Count > 0;
    }
}
