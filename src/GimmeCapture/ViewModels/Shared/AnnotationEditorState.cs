using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
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
                CommitPendingStyleEdit();
                ClearSelection();
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
    private bool _isSynchronizingSelectedStyle;
    private AnnotationSnapshot? _pendingStyleBefore;
    private Annotation? _pendingStyleAnnotation;
    private CancellationTokenSource? _styleCommitCancellation;

    public Color SelectedColor
    {
        get => _selectedColor;
        set => SetSelectedColor(value, commitImmediately: false);
    }

    private double _currentThickness = 2.0;
    public double CurrentThickness
    {
        get => _currentThickness;
        set
        {
            var clamped = Math.Clamp(value, 1, 30);
            if (_currentThickness.Equals(clamped)) return;
            this.RaiseAndSetIfChanged(ref _currentThickness, clamped);
            if (!_isSynchronizingSelectedStyle && SelectedAnnotation != null && SupportsStrokeStyle(SelectedAnnotation.Type))
            {
                var before = AnnotationSnapshot.Capture(SelectedAnnotation);
                SelectedAnnotation.Thickness = clamped;
                CommitAnnotationEdit(SelectedAnnotation, before);
            }
        }
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

    private Annotation? _selectedAnnotation;
    public Annotation? SelectedAnnotation
    {
        get => _selectedAnnotation;
        private set
        {
            if (ReferenceEquals(_selectedAnnotation, value)) return;
            _selectedAnnotation = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(StyleAnnotationTool));
        }
    }

    public AnnotationType StyleAnnotationTool => SelectedAnnotation?.Type ?? CurrentAnnotationTool;

    public void SelectTool(AnnotationType tool)
    {
        CurrentAnnotationTool = CurrentAnnotationTool == tool ? AnnotationType.None : tool;
    }

    public void SelectAnnotation(Annotation? annotation)
    {
        if (ReferenceEquals(SelectedAnnotation, annotation)) return;

        CommitPendingStyleEdit();
        if (SelectedAnnotation != null)
        {
            SelectedAnnotation.IsSelected = false;
        }

        SelectedAnnotation = annotation;
        if (annotation == null) return;

        annotation.IsSelected = AnnotationInteractionService.IsEditable(annotation.Type);
        _isSynchronizingSelectedStyle = true;
        try
        {
            if (SupportsStrokeStyle(annotation.Type))
            {
                this.RaiseAndSetIfChanged(ref _selectedColor, annotation.Color, nameof(SelectedColor));
                this.RaiseAndSetIfChanged(ref _currentThickness, annotation.Thickness, nameof(CurrentThickness));
            }
        }
        finally
        {
            _isSynchronizingSelectedStyle = false;
        }

        this.RaisePropertyChanged(nameof(CurrentRedactionPreset));
    }

    public void ClearSelection()
    {
        CommitPendingStyleEdit();
        if (SelectedAnnotation != null)
        {
            SelectedAnnotation.IsSelected = false;
        }
        SelectedAnnotation = null;
        this.RaisePropertyChanged(nameof(CurrentRedactionPreset));
    }

    public AnnotationSnapshot BeginAnnotationEdit(Annotation annotation)
    {
        CommitPendingStyleEdit();
        SelectAnnotation(annotation);
        return AnnotationSnapshot.Capture(annotation);
    }

    public void CommitAnnotationEdit(Annotation annotation, AnnotationSnapshot before)
    {
        var after = AnnotationSnapshot.Capture(annotation);
        if (!before.HasSameValues(after))
        {
            PushUndoAction(new AnnotationEditHistoryAction(annotation, before, after));
        }
    }

    public void CancelAnnotationEdit(Annotation annotation, AnnotationSnapshot before)
    {
        before.ApplyTo(annotation);
    }

    public void ToggleToolGroup(string group)
    {
        CurrentAnnotationTool = GetToolGroupTarget(group);
    }

    public AnnotationType GetToolGroupTarget(string group)
    {
        return group switch
        {
            "Shapes" => IsShapeToolActive ? AnnotationType.None : _lastShapeTool,
            "Pen" => IsPenToolActive ? AnnotationType.None : AnnotationType.Pen,
            "Text" => IsTextToolActive ? AnnotationType.None : AnnotationType.Text,
            "Redaction" => IsRedactionToolActive ? AnnotationType.None : _lastRedactionTool,
            _ => AnnotationType.None
        };
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
        AnnotationSnapshot? selectedBefore = null;
        if (SelectedAnnotation?.Type is AnnotationType.Mosaic or AnnotationType.Blur)
        {
            selectedBefore = AnnotationSnapshot.Capture(SelectedAnnotation);
        }

        var targetRedactionTool = SelectedAnnotation?.Type is AnnotationType.Mosaic or AnnotationType.Blur
            ? SelectedAnnotation.Type
            : (_lastRedactionTool == AnnotationType.Blur || CurrentAnnotationTool == AnnotationType.Blur
                ? AnnotationType.Blur
                : AnnotationType.Mosaic);

        if (targetRedactionTool == AnnotationType.Blur)
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

        if (SelectedAnnotation?.Type == AnnotationType.Blur)
        {
            SelectedAnnotation.EffectSettings = _blurSettings.Clone();
        }
        else if (SelectedAnnotation?.Type == AnnotationType.Mosaic)
        {
            SelectedAnnotation.EffectSettings = _mosaicSettings.Clone();
        }

        if (selectedBefore != null && SelectedAnnotation != null)
        {
            CommitAnnotationEdit(SelectedAnnotation, selectedBefore);
        }

        this.RaisePropertyChanged(nameof(CurrentRedactionPreset));
    }

    public string CurrentRedactionPreset
    {
        get
        {
            var tool = SelectedAnnotation?.Type
                ?? (CurrentAnnotationTool == AnnotationType.None ? _lastRedactionTool : CurrentAnnotationTool);
            if (tool == AnnotationType.Blur)
            {
                var radius = SelectedAnnotation?.Type == AnnotationType.Blur
                    ? SelectedAnnotation.EffectSettings.BlurRadius
                    : _blurSettings.BlurRadius;
                return radius switch
                {
                    <= 12f => "Soft",
                    >= 24f => "Strong",
                    _ => "Medium"
                };
            }

            var cellSize = SelectedAnnotation?.Type == AnnotationType.Mosaic
                ? SelectedAnnotation.EffectSettings.MosaicCellSize
                : _mosaicSettings.MosaicCellSize;
            return cellSize switch
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
        if (AnnotationInteractionService.IsEditable(annotation.Type))
        {
            SelectAnnotation(annotation);
        }
    }

    public void BeginPendingAnnotation(Annotation annotation)
    {
        if (annotation.Type is AnnotationType.Mosaic or AnnotationType.Blur)
        {
            annotation.EffectSettings = CreateEffectSettingsFor(annotation.Type);
        }

        Annotations.Add(annotation);
    }

    public bool CommitPendingAnnotation(Annotation annotation)
    {
        if (!Annotations.Contains(annotation))
        {
            return false;
        }

        if (!AnnotationInteractionService.IsValidCompletedAnnotation(annotation))
        {
            Annotations.Remove(annotation);
            if (ReferenceEquals(SelectedAnnotation, annotation))
            {
                ClearSelection();
            }
            return false;
        }

        PushUndoAction(new AnnotationHistoryAction(Annotations, annotation, true));
        if (AnnotationInteractionService.IsEditable(annotation.Type))
        {
            SelectAnnotation(annotation);
        }
        return true;
    }

    public void CancelPendingAnnotation(Annotation annotation)
    {
        Annotations.Remove(annotation);
        if (ReferenceEquals(SelectedAnnotation, annotation))
        {
            ClearSelection();
        }
    }

    public void RemoveAnnotation(Annotation annotation)
    {
        if (ReferenceEquals(SelectedAnnotation, annotation))
        {
            ClearSelection();
        }
        Annotations.Remove(annotation);
        PushUndoAction(new AnnotationHistoryAction(Annotations, annotation, false));
    }

    public bool RemoveSelectedAnnotation()
    {
        var annotation = SelectedAnnotation;
        if (annotation == null)
        {
            return false;
        }

        RemoveAnnotation(annotation);
        return true;
    }

    public void ClearAnnotations()
    {
        if (Annotations.Count == 0) return;
        ClearSelection();
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
        CommitPendingStyleEdit();
        if (_historyStack.Count == 0) return;
        var action = _historyStack.Pop();
        action.Undo();
        if (SelectedAnnotation != null && !Annotations.Contains(SelectedAnnotation))
        {
            ClearSelection();
        }
        _redoHistoryStack.Push(action);
        UpdateHistoryStatus();
    }

    public void Redo()
    {
        CommitPendingStyleEdit();
        if (_redoHistoryStack.Count == 0) return;
        var action = _redoHistoryStack.Pop();
        action.Redo();
        if (SelectedAnnotation != null && !Annotations.Contains(SelectedAnnotation))
        {
            ClearSelection();
        }
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
        this.RaisePropertyChanged(nameof(StyleAnnotationTool));
    }

    private static bool IsShapeTool(AnnotationType tool)
        => tool is AnnotationType.Rectangle or AnnotationType.Ellipse or AnnotationType.Arrow or AnnotationType.Line;

    private static bool IsRedactionTool(AnnotationType tool)
        => tool is AnnotationType.Mosaic or AnnotationType.Blur;

    private static bool SupportsStrokeStyle(AnnotationType tool)
        => tool is AnnotationType.Rectangle or AnnotationType.Ellipse or AnnotationType.Arrow or AnnotationType.Line;

    public void ApplySelectedColor(Color color)
    {
        SetSelectedColor(color, commitImmediately: true);
    }

    public void ApplySelectedThickness(double thickness)
    {
        CurrentThickness = thickness;
    }

    public void CommitPendingStyleEdit()
    {
        _styleCommitCancellation?.Cancel();
        _styleCommitCancellation?.Dispose();
        _styleCommitCancellation = null;

        if (_pendingStyleBefore != null && _pendingStyleAnnotation != null)
        {
            CommitAnnotationEdit(_pendingStyleAnnotation, _pendingStyleBefore);
        }

        _pendingStyleBefore = null;
        _pendingStyleAnnotation = null;
    }

    private void SetSelectedColor(Color value, bool commitImmediately)
    {
        if (_selectedColor == value) return;
        this.RaiseAndSetIfChanged(ref _selectedColor, value);
        if (_isSynchronizingSelectedStyle || SelectedAnnotation == null || !SupportsStrokeStyle(SelectedAnnotation.Type))
        {
            return;
        }

        if (_pendingStyleBefore == null || !ReferenceEquals(_pendingStyleAnnotation, SelectedAnnotation))
        {
            CommitPendingStyleEdit();
            _pendingStyleAnnotation = SelectedAnnotation;
            _pendingStyleBefore = AnnotationSnapshot.Capture(SelectedAnnotation);
        }

        SelectedAnnotation.Color = value;
        if (commitImmediately)
        {
            CommitPendingStyleEdit();
        }
        else
        {
            ScheduleStyleCommit();
        }
    }

    private void ScheduleStyleCommit()
    {
        _styleCommitCancellation?.Cancel();
        _styleCommitCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _styleCommitCancellation = cancellation;
        CommitStyleAfterDelayAsync(cancellation.Token).Forget("Annotation.CommitStyle");
    }

    private async Task CommitStyleAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    CommitPendingStyleEdit();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        CommitPendingStyleEdit();
        ClearSelection();
        foreach (var action in _historyStack) action.Dispose();
        _historyStack.Clear();

        foreach (var action in _redoHistoryStack) action.Dispose();
        _redoHistoryStack.Clear();
    }
}
