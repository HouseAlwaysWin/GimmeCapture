using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Shared;

public sealed class AnnotationEditorState : ReactiveObject, IDisposable
{
    private static readonly AnnotationEffectSettings MosaicSmall = new() { MosaicCellSize = 18, BlurRadius = 24f, Feather = 0f };
    private static readonly AnnotationEffectSettings MosaicMedium = new() { MosaicCellSize = 30, BlurRadius = 24f, Feather = 0f };
    private static readonly AnnotationEffectSettings MosaicLarge = new() { MosaicCellSize = 48, BlurRadius = 24f, Feather = 0f };
    private static readonly AnnotationEffectSettings BlurSoft = new() { MosaicCellSize = 20, BlurRadius = 10f, Feather = 0f };
    private static readonly AnnotationEffectSettings BlurMedium = new() { MosaicCellSize = 20, BlurRadius = 18f, Feather = 0f };
    private static readonly AnnotationEffectSettings BlurStrong = new() { MosaicCellSize = 20, BlurRadius = 28f, Feather = 0f };

    public ObservableCollection<Annotation> Annotations { get; } = new();

    private AnnotationType _currentAnnotationTool = AnnotationType.None;
    public AnnotationType CurrentAnnotationTool
    {
        get => _currentAnnotationTool;
        set
        {
            if (_currentAnnotationTool != value)
            {
                this.RaiseAndSetIfChanged(ref _currentAnnotationTool, value);
                if (IsShapeTool(value)) _lastShapeTool = value;
                if (IsRedactionTool(value)) _lastRedactionTool = value;
                RaiseToolStateChanged();
            }
        }
    }

    public bool IsShapeToolActive => IsShapeTool(CurrentAnnotationTool);
    public bool IsPenToolActive => CurrentAnnotationTool == AnnotationType.Pen;
    public bool IsTextToolActive => CurrentAnnotationTool == AnnotationType.Text;
    public bool IsRedactionToolActive => IsRedactionTool(CurrentAnnotationTool);

    private Color _selectedColor = Colors.Red;
    public Color SelectedColor
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

    private FontFamily _currentFontFamily = new("Arial");
    public FontFamily CurrentFontFamily
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

    private bool _isEnteringText;
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

    private readonly Stack<IHistoryAction> _historyStack = new();
    private readonly Stack<IHistoryAction> _redoHistoryStack = new();
    public Stack<IHistoryAction> HistoryStack => _historyStack;
    public Stack<IHistoryAction> RedoHistoryStack => _redoHistoryStack;

    private bool _hasUndo;
    public bool HasUndo
    {
        get => _hasUndo;
        private set => this.RaiseAndSetIfChanged(ref _hasUndo, value);
    }

    private bool _hasRedo;
    public bool HasRedo
    {
        get => _hasRedo;
        private set => this.RaiseAndSetIfChanged(ref _hasRedo, value);
    }

    private AnnotationType _lastShapeTool = AnnotationType.Rectangle;
    private AnnotationType _lastRedactionTool = AnnotationType.Mosaic;
    private AnnotationEffectSettings _mosaicSettings = MosaicMedium.Clone();
    private AnnotationEffectSettings _blurSettings = BlurMedium.Clone();

    public void SelectTool(AnnotationType tool)
    {
        CurrentAnnotationTool = CurrentAnnotationTool == tool ? AnnotationType.None : tool;
    }

    public void ToggleToolGroup(string group)
    {
        AnnotationType targetTool = group switch
        {
            "Shapes" => IsShapeToolActive ? AnnotationType.None : _lastShapeTool,
            "Pen" => IsPenToolActive ? AnnotationType.None : AnnotationType.Pen,
            "Text" => IsTextToolActive ? AnnotationType.None : AnnotationType.Text,
            "Redaction" => IsRedactionToolActive ? AnnotationType.None : _lastRedactionTool,
            _ => AnnotationType.None
        };

        CurrentAnnotationTool = targetTool;
    }

    public AnnotationEffectSettings CreateEffectSettingsFor(AnnotationType tool)
    {
        return tool switch
        {
            AnnotationType.Mosaic => _mosaicSettings.Clone(),
            AnnotationType.Blur => _blurSettings.Clone(),
            _ => new AnnotationEffectSettings()
        };
    }

    public Annotation CreateAnnotationForCurrentTool(Point startPoint, Bitmap? drawingModeSnapshot = null, Size drawingModeReferenceSize = default)
    {
        var annotation = new Annotation
        {
            Type = CurrentAnnotationTool,
            StartPoint = startPoint,
            EndPoint = startPoint,
            Color = SelectedColor,
            Thickness = CurrentThickness,
            FontSize = CurrentFontSize,
            FontFamily = CurrentFontFamily,
            IsBold = IsBold,
            IsItalic = IsItalic,
            DrawingModeSnapshot = drawingModeSnapshot,
            DrawingModeReferenceSize = drawingModeReferenceSize,
            EffectSettings = CreateEffectSettingsFor(CurrentAnnotationTool)
        };

        if (CurrentAnnotationTool == AnnotationType.Pen)
        {
            annotation.AddPoint(startPoint);
        }

        return annotation;
    }

    public void SetRedactionPreset(string preset)
    {
        if (_lastRedactionTool == AnnotationType.Blur || CurrentAnnotationTool == AnnotationType.Blur)
        {
            _blurSettings = preset switch
            {
                "Soft" => BlurSoft.Clone(),
                "Strong" => BlurStrong.Clone(),
                _ => BlurMedium.Clone()
            };
        }
        else
        {
            _mosaicSettings = preset switch
            {
                "Small" => MosaicSmall.Clone(),
                "Large" => MosaicLarge.Clone(),
                _ => MosaicMedium.Clone()
            };
        }

        this.RaisePropertyChanged(nameof(CurrentRedactionPreset));
    }

    public string CurrentRedactionPreset
    {
        get
        {
            var tool = CurrentAnnotationTool == AnnotationType.None ? _lastRedactionTool : CurrentAnnotationTool;
            if (tool == AnnotationType.Blur)
            {
                return _blurSettings.BlurRadius switch
                {
                    <= 12f => "Soft",
                    >= 24f => "Strong",
                    _ => "Medium"
                };
            }

            return _mosaicSettings.MosaicCellSize switch
            {
                <= 18 => "Small",
                >= 48 => "Large",
                _ => "Medium"
            };
        }
    }

    public void AddAnnotation(Annotation annotation)
    {
        if (annotation.Type is AnnotationType.Mosaic or AnnotationType.Blur)
        {
            annotation.EffectSettings = CreateEffectSettingsFor(annotation.Type);
        }

        Annotations.Add(annotation);
        PushUndoAction(new AnnotationHistoryAction(Annotations, annotation, true));
    }

    public void RemoveAnnotation(Annotation annotation)
    {
        Annotations.Remove(annotation);
        PushUndoAction(new AnnotationHistoryAction(Annotations, annotation, false));
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
        foreach (var redoAction in _redoHistoryStack)
        {
            redoAction.Dispose();
        }
        _redoHistoryStack.Clear();
        UpdateHistoryStatus();
    }

    public void Undo()
    {
        if (_historyStack.Count == 0) return;
        var action = _historyStack.Pop();
        action.Undo();
        _redoHistoryStack.Push(action);
        UpdateHistoryStatus();
    }

    public void Redo()
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

    private void RaiseToolStateChanged()
    {
        this.RaisePropertyChanged(nameof(IsShapeToolActive));
        this.RaisePropertyChanged(nameof(IsPenToolActive));
        this.RaisePropertyChanged(nameof(IsTextToolActive));
        this.RaisePropertyChanged(nameof(IsRedactionToolActive));
        this.RaisePropertyChanged(nameof(CurrentRedactionPreset));
    }

    private static bool IsShapeTool(AnnotationType tool)
        => tool is AnnotationType.Rectangle or AnnotationType.Ellipse or AnnotationType.Arrow or AnnotationType.Line;

    private static bool IsRedactionTool(AnnotationType tool)
        => tool is AnnotationType.Mosaic or AnnotationType.Blur;

    public void Dispose()
    {
        foreach (var action in _historyStack) action.Dispose();
        _historyStack.Clear();

        foreach (var action in _redoHistoryStack) action.Dispose();
        _redoHistoryStack.Clear();
    }
}
