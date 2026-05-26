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
using GimmeCapture.Services.Core.Media;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    private readonly AnnotationEditorState _editorState = new();

    // Annotation Properties
    public ObservableCollection<Annotation> Annotations => _editorState.Annotations;

    public AnnotationType CurrentAnnotationTool
    {
        get => _editorState.CurrentAnnotationTool;
        set => _editorState.CurrentAnnotationTool = value;
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
                // Entering drawing mode - capture snapshot
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () => 
                {
                    try
                    {
                        var snapshot = CaptureDrawingModeSnapshotAsync != null
                            ? await CaptureDrawingModeSnapshotAsync()
                            : await _captureService.CaptureRegionBitmapAsync(SelectionRect, ScreenOffset, VisualScaling);
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
        _editorState.ToggleToolGroup(group);
        IsDrawingMode = CurrentAnnotationTool != AnnotationType.None;
    }

    // Commands
    public ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; set; } = null!;
    public ReactiveCommand<string, Unit> ToggleToolGroupCommand { get; set; } = null!;
    public ReactiveCommand<Color, Unit> ChangeColorCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> UndoCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> RedoCommand { get; set; } = null!;
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
    public ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleToolbarCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> SelectFullscreenCommand { get; set; } = null!;
    public string CurrentRedactionPreset => _editorState.CurrentRedactionPreset;

    private void InitializeToolbarCommands()
    {
        var canExecuteHotkeys = this.WhenAnyValue(x => x.IsInputFocused, x => !x);
        var canExecuteNonTranslation = this.WhenAnyValue(
            x => x.IsInputFocused, x => x.CurrentMode,
            (focused, mode) => !focused && mode != SnipMode.Translation);

        ConfirmTextEntryCommand = ReactiveCommand.Create(() => 
        {
            if (!string.IsNullOrWhiteSpace(PendingText))
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
            IsEnteringText = false;
            PendingText = string.Empty;
            FocusWindowAction?.Invoke();
        });

        ClearAnnotationsCommand = ReactiveCommand.Create(ClearAnnotations, canExecuteNonTranslation);
        
        ToggleToolGroupCommand = ReactiveCommand.Create<string>(ToggleToolGroup, canExecuteNonTranslation);
        SetRedactionPresetCommand = ReactiveCommand.Create<string>(preset => _editorState.SetRedactionPreset(preset), canExecuteNonTranslation);
        
        SelectToolCommand = ReactiveCommand.Create<AnnotationType>(t => {
            _editorState.SelectTool(t);
            IsDrawingMode = CurrentAnnotationTool != AnnotationType.None;
        }, canExecuteNonTranslation);
        SelectToolCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        ChangeColorCommand = ReactiveCommand.Create<Color>(c => SelectedColor = c);
        ChangeColorCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        IncreaseThicknessCommand = ReactiveCommand.Create(() => { CurrentThickness = Math.Min(CurrentThickness + 1, 30); });
        DecreaseThicknessCommand = ReactiveCommand.Create(() => { CurrentThickness = Math.Max(CurrentThickness - 1, 1); });
        
        var canUndo = this.WhenAnyValue(x => x.HasUndo, x => x.IsInputFocused, (u, textFocus) => u && !textFocus);
        var canRedo = this.WhenAnyValue(x => x.HasRedo, x => x.IsInputFocused, (u, textFocus) => u && !textFocus);
        UndoCommand = ReactiveCommand.Create(Undo, canUndo);
        UndoCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        RedoCommand = ReactiveCommand.Create(Redo, canRedo);
        RedoCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

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
            catch { }
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

        IncreaseCornerIconScaleCommand = ReactiveCommand.Create(() => { if (CornerIconScale < 1.0) CornerIconScale = Math.Round(CornerIconScale + 0.1, 1); });
        IncreaseCornerIconScaleCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        DecreaseCornerIconScaleCommand = ReactiveCommand.Create(() => { if (CornerIconScale > 0.4) CornerIconScale = Math.Round(CornerIconScale - 0.1, 1); });
        DecreaseCornerIconScaleCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

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

            IsDrawingMode = false;
            CurrentState = SnipState.Selected;
            SelectionRect = ResolveFullscreenSelectionRect();
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
}
