using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GimmeCapture.Models;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    private readonly AnnotationEditorState _editorState = new();

    // Annotation Properties
    public ObservableCollection<Annotation> Annotations => _editorState.Annotations;
    public Annotation? SelectedAnnotation => _editorState.SelectedAnnotation;
    public AnnotationType StyleAnnotationTool => _editorState.StyleAnnotationTool;

    public AnnotationType CurrentAnnotationTool
    {
        get => _editorState.CurrentAnnotationTool;
        set
        {
            if (_editorState.CurrentAnnotationTool == value) return;

            if (_editorState.IsEnteringText)
            {
                _editorState.IsEnteringText = false;
                _editorState.PendingText = string.Empty;
            }

            _editorState.CurrentAnnotationTool = value;
        }
    }

    public bool IsShapeToolActive => _editorState.IsShapeToolActive;
    public bool IsPenToolActive => _editorState.IsPenToolActive;
    public bool IsTextToolActive => _editorState.IsTextToolActive;
    public bool IsRedactionToolActive => _editorState.IsRedactionToolActive;

    private bool _isDrawingMode = false;
    public bool IsDrawingMode
    {
        get => _isDrawingMode;
        set
        {
            if (value && !_isDrawingMode)
            {
                BeginDrawingModeSnapshotCapture(forceCapture: DrawingModeSnapshot == null);
            }
            else if (!value && _isDrawingMode)
            {
                // Keep the frozen selection snapshot when the normal snip flow is locked.
                if (!IsSelectionSnapshotLocked)
                {
                    DisposeDrawingModeSnapshot();
                }
            }
            this.RaiseAndSetIfChanged(ref _isDrawingMode, value);
            this.RaisePropertyChanged(nameof(IsHoverPreviewVisible));

            if (value)
            {
                DismissWindowSnapHoverPreview(preserveTargetRect: false);
            }
        }
    }

    private Avalonia.Media.Imaging.Bitmap? _drawingModeSnapshot;
    public Avalonia.Media.Imaging.Bitmap? DrawingModeSnapshot
    {
        get => _drawingModeSnapshot;
        set => this.RaiseAndSetIfChanged(ref _drawingModeSnapshot, value);
    }

    public Task<Avalonia.Media.Imaging.WriteableBitmap?> CaptureRegionBitmapAsync()
    {
        return _captureService.CaptureRegionBitmapAsync(SelectionRect, ScreenOffset, VisualScaling);
    }

    private bool _lockSelectedScreenshotSelection;
    public bool LockSelectedScreenshotSelection
    {
        get => _lockSelectedScreenshotSelection;
        set
        {
            if (_lockSelectedScreenshotSelection == value) return;
            this.RaiseAndSetIfChanged(ref _lockSelectedScreenshotSelection, value);
            RefreshSelectedSnapshotLock();
        }
    }

    /// <summary>
    /// Toolbar toggle for "lock selected screenshot instead of click-through". Flips the live lock on the
    /// current selection (its setter freezes / unfreezes the snapshot on the spot) and persists the choice
    /// back to the AutoPinScreenshotSelection setting, so the toolbar button and the settings checkbox stay
    /// the same switch.
    /// </summary>
    private void ToggleLockSelectionSnapshot()
    {
        bool newValue = !LockSelectedScreenshotSelection;
        LockSelectedScreenshotSelection = newValue;
        if (_mainVm != null)
        {
            _mainVm.AutoPinScreenshotSelection = newValue;
        }
    }

    private bool _isSelectionSnapshotLocked;
    public bool IsSelectionSnapshotLocked
    {
        get => _isSelectionSnapshotLocked;
        private set
        {
            if (_isSelectionSnapshotLocked == value) return;
            this.RaiseAndSetIfChanged(ref _isSelectionSnapshotLocked, value);
            RefreshInteractionRegion();
        }
    }

    private int _selectionSnapshotCaptureVersion;

    public void RefreshSelectedSnapshotLock(bool forceCapture = false)
    {
        if (ShouldLockSelectedSnapshot())
        {
            BeginSelectedSnapshotLockCapture(forceCapture);
        }
        else
        {
            ClearSelectedSnapshotLock();
        }
    }

    private bool ShouldLockSelectedSnapshot()
    {
        return LockSelectedScreenshotSelection
            && CurrentMode == SnipMode.Screenshot
            && CurrentState == SnipState.Selected
            && SelectionRect.Width > 1
            && SelectionRect.Height > 1;
    }

    private void BeginSelectedSnapshotLockCapture(bool forceCapture)
    {
        IsSelectionSnapshotLocked = true;

        if (DrawingModeSnapshot != null && !forceCapture)
        {
            return;
        }

        if (forceCapture)
        {
            DisposeDrawingModeSnapshot();
        }

        BeginDrawingModeSnapshotCapture(forceCapture: true, lockSnapshot: true);
    }

    private void ClearSelectedSnapshotLock()
    {
        _selectionSnapshotCaptureVersion++;
        IsSelectionSnapshotLocked = false;

        if (!IsDrawingMode)
        {
            DisposeDrawingModeSnapshot();
        }
    }

    private void BeginDrawingModeSnapshotCapture(bool forceCapture, bool lockSnapshot = false)
    {
        if (!forceCapture && DrawingModeSnapshot != null)
        {
            return;
        }

        int captureVersion = ++_selectionSnapshotCaptureVersion;
        _ = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            Avalonia.Media.Imaging.WriteableBitmap? snapshot = null;
            try
            {
                snapshot = CaptureDrawingModeSnapshotAsync != null
                    ? await CaptureDrawingModeSnapshotAsync()
                    : await _captureService.CaptureRegionBitmapAsync(SelectionRect, ScreenOffset, VisualScaling);

                if (snapshot == null)
                {
                    if (lockSnapshot)
                    {
                        IsSelectionSnapshotLocked = false;
                    }
                    return;
                }

                if (captureVersion != _selectionSnapshotCaptureVersion
                    || (lockSnapshot && !ShouldLockSelectedSnapshot()))
                {
                    snapshot.Dispose();
                    return;
                }

                DisposeDrawingModeSnapshot();
                DrawingModeSnapshot = snapshot;
                snapshot = null;
            }
            catch (Exception ex)
            {
                AppLog.Error("SnipWindow.DrawingSnapshot", ex);
                if (lockSnapshot)
                {
                    IsSelectionSnapshotLocked = false;
                }
            }
            finally
            {
                snapshot?.Dispose();
            }
        });
    }

    private void DisposeDrawingModeSnapshot()
    {
        if (_drawingModeSnapshot == null)
        {
            return;
        }

        var temp = _drawingModeSnapshot;
        DrawingModeSnapshot = null;
        temp.Dispose();
    }

    public Color SelectedColor
    {
        get => _editorState.SelectedColor;
        set => _editorState.SelectedColor = value;
    }

    private string _customHexColor = "#FF0000";
    public string CustomHexColor
    {
        get => _customHexColor;
        set => this.RaiseAndSetIfChanged(ref _customHexColor, value);
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

    public bool ShowIconSettings => true;

    public FontFamily CurrentFontFamily
    {
        get => _editorState.CurrentFontFamily;
        set => _editorState.CurrentFontFamily = value;
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

    public bool IsShapeFilled
    {
        get => _editorState.CurrentFill;
        set => _editorState.CurrentFill = value;
    }

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

    private bool _isBackgroundRemoved;
    public bool IsBackgroundRemoved
    {
        get => _isBackgroundRemoved;
        set => this.RaiseAndSetIfChanged(ref _isBackgroundRemoved, value);
    }

    public bool IsEnteringText
    {
        get => _editorState.IsEnteringText;
        set => _editorState.IsEnteringText = value;
    }
    
    public Point TextInputPosition
    {
        get => _editorState.TextInputPosition;
        set => _editorState.TextInputPosition = value;
    }

    public string PendingText
    {
        get => _editorState.PendingText;
        set => _editorState.PendingText = value;
    }

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

    private void UpdateHistoryStatus()
    {
        HasUndo = _editorState.HasUndo;
        HasRedo = _editorState.HasRedo;
    }

    private void Undo()
    {
        _editorState.Undo();
        UpdateHistoryStatus();
    }

    private void Redo()
    {
        _editorState.Redo();
        UpdateHistoryStatus();
    }

    private void PushUndoAction(IHistoryAction action)
    {
        _editorState.PushUndoAction(action);
        UpdateHistoryStatus();
    }

    public void AddAnnotation(Annotation annotation)
    {
        _editorState.AddAnnotation(annotation);
        UpdateHistoryStatus();
    }

    public void BeginPendingAnnotation(Annotation annotation) => _editorState.BeginPendingAnnotation(annotation);

    public bool CommitPendingAnnotation(Annotation annotation)
    {
        var committed = _editorState.CommitPendingAnnotation(annotation);
        UpdateHistoryStatus();
        return committed;
    }

    public void CancelPendingAnnotation(Annotation annotation) => _editorState.CancelPendingAnnotation(annotation);

    // A Callout leader drawn by dragging, now pending in the annotation list and awaiting its
    // label text from the text-entry overlay (drag-the-line-then-type flow).
    private Annotation? _pendingTextLeader;

    public void BeginCalloutTextEntry(Annotation leader) => _pendingTextLeader = leader;

    public void SelectAnnotation(Annotation? annotation) => _editorState.SelectAnnotation(annotation);

    public void ClearAnnotationSelection() => _editorState.ClearSelection();

    public AnnotationSnapshot BeginAnnotationEdit(Annotation annotation) => _editorState.BeginAnnotationEdit(annotation);

    public void CommitAnnotationEdit(Annotation annotation, AnnotationSnapshot before)
    {
        _editorState.CommitAnnotationEdit(annotation, before);
        UpdateHistoryStatus();
    }

    public Annotation CreateAnnotationForCurrentTool(Point relPoint)
    {
        return _editorState.CreateAnnotationForCurrentTool(
            relPoint,
            DrawingModeSnapshot,
            SelectionRect.Size);
    }

    public void RemoveAnnotation(Annotation annotation)
    {
        _editorState.RemoveAnnotation(annotation);
        UpdateHistoryStatus();
    }

    private void ClearAnnotations()
    {
        _editorState.ClearAnnotations();
        UpdateHistoryStatus();
    }

    public void ToggleToolGroup(string group)
    {
        CurrentAnnotationTool = _editorState.GetToolGroupTarget(group);
        IsDrawingMode = CurrentAnnotationTool != AnnotationType.None;
    }

    private void DeactivateDrawingInteraction()
    {
        _editorState.CurrentAnnotationTool = AnnotationType.None;
        IsDrawingMode = false;
        IsEnteringText = false;
        PendingText = string.Empty;
    }

    // Commands
    public ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; set; } = null!;
    public ReactiveCommand<string, Unit> ToggleToolGroupCommand { get; set; } = null!;
    public ReactiveCommand<Color, Unit> ChangeColorCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> UndoCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> RedoCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> BringToFrontCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SendToBackCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ClearAnnotationsCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ConfirmTextEntryCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> CancelTextEntryCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseFontSizeCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ApplyHexColorCommand { get; set; } = null!;
    public ReactiveCommand<string, Unit> SetRedactionPresetCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ChangeLanguageCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleBoldCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleItalicCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleToolbarCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SelectFullscreenCommand { get; set; } = null!;
    public string CurrentRedactionPreset => _editorState.CurrentRedactionPreset;

    private void InitializeToolbarCommands()
    {
        var canExecuteHotkeys = this.WhenAnyValue(
            x => x.IsInputFocused,
            x => x.IsEnteringText,
            (focused, enteringText) => !focused && !enteringText);
        // Pointer-driven toolbar commands must remain available while a text box owns focus.
        // Keyboard routing separately blocks drawing hotkeys during text entry.
        var canExecuteNonTranslation = this.WhenAnyValue(
            x => x.CurrentMode,
            mode => mode != SnipMode.Translation);

        ConfirmTextEntryCommand = ReactiveCommand.Create(() =>
        {
            if (_pendingTextLeader != null)
            {
                // Callout: the leader was already drawn by dragging and is pending in the list.
                // Attach the typed label, sync the current style, and commit it.
                var leader = _pendingTextLeader;
                _pendingTextLeader = null;
                leader.Text = PendingText ?? string.Empty;
                leader.Color = SelectedColor;
                leader.Thickness = CurrentThickness;
                leader.FontSize = CurrentFontSize;
                leader.FontFamily = CurrentFontFamily;
                leader.IsBold = IsBold;
                leader.IsItalic = IsItalic;
                if (!CommitPendingAnnotation(leader))
                {
                    CancelPendingAnnotation(leader);
                }
            }
            else if (!string.IsNullOrWhiteSpace(PendingText))
            {
                var relPoint = new Point(TextInputPosition.X - SelectionRect.X, TextInputPosition.Y - SelectionRect.Y);
                AddAnnotation(new Annotation
                {
                    Type = AnnotationType.Text,
                    StartPoint = relPoint,
                    EndPoint = relPoint,
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
            if (_pendingTextLeader != null)
            {
                // Discard the drawn-but-unconfirmed Callout leader.
                CancelPendingAnnotation(_pendingTextLeader);
                _pendingTextLeader = null;
            }
            IsEnteringText = false;
            PendingText = string.Empty;
            FocusWindowAction?.Invoke();
        });

        ClearAnnotationsCommand = ReactiveCommand.Create(ClearAnnotations, canExecuteNonTranslation);
        
        ToggleToolGroupCommand = ReactiveCommand.Create<string>(ToggleToolGroup, canExecuteNonTranslation);
        SetRedactionPresetCommand = ReactiveCommand.Create<string>(preset => _editorState.SetRedactionPreset(preset), canExecuteNonTranslation);
        
        SelectToolCommand = ReactiveCommand.Create<AnnotationType>(t => {
            CurrentAnnotationTool = CurrentAnnotationTool == t ? AnnotationType.None : t;
            IsDrawingMode = CurrentAnnotationTool != AnnotationType.None;
        }, canExecuteNonTranslation);
        SelectToolCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        ChangeColorCommand = ReactiveCommand.Create<Color>(_editorState.ApplySelectedColor);
        ChangeColorCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        IncreaseThicknessCommand = ReactiveCommand.Create(() => _editorState.ApplySelectedThickness(Math.Min(CurrentThickness + 1, 30)));
        DecreaseThicknessCommand = ReactiveCommand.Create(() => _editorState.ApplySelectedThickness(Math.Max(CurrentThickness - 1, 1)));
        
        var canUndo = this.WhenAnyValue(
            x => x.HasUndo,
            x => x.IsInputFocused,
            x => x.IsEnteringText,
            (undo, textFocus, enteringText) => undo && !textFocus && !enteringText);
        var canRedo = this.WhenAnyValue(
            x => x.HasRedo,
            x => x.IsInputFocused,
            x => x.IsEnteringText,
            (redo, textFocus, enteringText) => redo && !textFocus && !enteringText);
        UndoCommand = ReactiveCommand.Create(Undo, canUndo);
        UndoCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        RedoCommand = ReactiveCommand.Create(Redo, canRedo);
        RedoCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        var canReorder = this.WhenAnyValue(
            x => x.SelectedAnnotation,
            x => x.IsInputFocused,
            x => x.IsEnteringText,
            (annotation, textFocus, enteringText) => annotation != null && !textFocus && !enteringText);
        BringToFrontCommand = ReactiveCommand.Create(() => { _editorState.BringSelectedToFront(); }, canReorder);
        BringToFrontCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        SendToBackCommand = ReactiveCommand.Create(() => { _editorState.SendSelectedToBack(); }, canReorder);
        SendToBackCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        IncreaseFontSizeCommand = ReactiveCommand.Create(() => { if (CurrentFontSize < 72) CurrentFontSize += 2; }, canExecuteHotkeys);
        IncreaseFontSizeCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        DecreaseFontSizeCommand = ReactiveCommand.Create(() => { if (CurrentFontSize > 8) CurrentFontSize -= 2; }, canExecuteHotkeys);
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
            catch (Exception ex) { AppLog.Warning("SnipToolbar.ApplyHexColor", ex); }
        });
        ApplyHexColorCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        ChangeLanguageCommand = ReactiveCommand.Create(() => LocalizationService.Instance.CycleLanguage());
        ChangeLanguageCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        ToggleBoldCommand = ReactiveCommand.Create(() => 
        {
            IsBold = !IsBold;
            return Unit.Default;
        });
        ToggleBoldCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        ToggleItalicCommand = ReactiveCommand.Create(() => 
        {
            IsItalic = !IsItalic;
            return Unit.Default;
        });
        ToggleItalicCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        IncreaseWingScaleCommand = ReactiveCommand.Create(() => { if (WingScale < 3.0) WingScale = Math.Round(WingScale + 0.1, 1); });
        IncreaseWingScaleCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        DecreaseWingScaleCommand = ReactiveCommand.Create(() => { if (WingScale > 0.5) WingScale = Math.Round(WingScale - 0.1, 1); });
        DecreaseWingScaleCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        ToggleToolbarCommand = ReactiveCommand.Create(() => { ShowToolbar = !ShowToolbar; });
        ToggleToolbarCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        ToggleTranslationResultsCommand = ReactiveCommand.Create(() => { ShowTranslationResults = !ShowTranslationResults; });
        ToggleTranslationResultsCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

        SelectFullscreenCommand = ReactiveCommand.Create(() =>
        {
            if (CurrentMode == SnipMode.Translation || RecState != RecordingState.Idle)
            {
                return;
            }

            DeactivateDrawingInteraction();
            SelectionRect = ResolveFullscreenSelectionRect();
            CurrentState = SnipState.Selected;
        }, canExecuteNonTranslation);
        SelectFullscreenCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
    }

    private Rect ResolveFullscreenSelectionRect()
    {
        if (ActiveScreenBounds.Width > 0 && ActiveScreenBounds.Height > 0)
        {
            return ActiveScreenBounds;
        }

        if (AllScreenBounds is { Count: > 0 })
        {
            var first = AllScreenBounds[0];
            if (first.W > 0 && first.H > 0)
            {
                return new Rect(first.X, first.Y, first.W, first.H);
            }
        }

        double width = ViewportSize.Width > 0 ? ViewportSize.Width : 1920;
        double height = ViewportSize.Height > 0 ? ViewportSize.Height : 1080;
        return new Rect(0, 0, width, height);
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

    public double WingWidth => 100 * WingScale;
    public double WingHeight => 60 * WingScale;
    public double SelectionIconSize => 8;
    public Thickness LeftWingMargin => new Thickness(-WingWidth, 0, 0, 0);
    public Thickness RightWingMargin => new Thickness(0, 0, -WingWidth, 0);
}
