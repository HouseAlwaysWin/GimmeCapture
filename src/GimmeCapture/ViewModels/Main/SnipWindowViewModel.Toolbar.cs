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
using GimmeCapture.Services.Core;
using System.Linq;

namespace GimmeCapture.ViewModels.Main;

public partial class SnipWindowViewModel
{
    // Annotation Properties
    public ObservableCollection<Annotation> Annotations { get; } = new();

    private AnnotationType _currentAnnotationTool = AnnotationType.None;
    public AnnotationType CurrentAnnotationTool
    {
        get => _currentAnnotationTool;
        set 
        {
            this.RaiseAndSetIfChanged(ref _currentAnnotationTool, value);
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsPenToolActive));
            this.RaisePropertyChanged(nameof(IsTextToolActive));
        }
    }

    public bool IsShapeToolActive => CurrentAnnotationTool == AnnotationType.Rectangle || CurrentAnnotationTool == AnnotationType.Ellipse || CurrentAnnotationTool == AnnotationType.Arrow || CurrentAnnotationTool == AnnotationType.Line || CurrentAnnotationTool == AnnotationType.Mosaic || CurrentAnnotationTool == AnnotationType.Blur;
    public bool IsPenToolActive => CurrentAnnotationTool == AnnotationType.Pen;
    public bool IsTextToolActive => CurrentAnnotationTool == AnnotationType.Text;

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

    private Avalonia.Media.Imaging.Bitmap? _drawingModeSnapshot;
    public Avalonia.Media.Imaging.Bitmap? DrawingModeSnapshot
    {
        get => _drawingModeSnapshot;
        set => this.RaiseAndSetIfChanged(ref _drawingModeSnapshot, value);
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

    public bool ShowIconSettings => true;

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

    // History
    private Stack<IHistoryAction> _historyStack = new();
    private Stack<IHistoryAction> _redoHistoryStack = new();
    private bool _isUndoingOrRedoing = false;

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
        HasUndo = _historyStack.Count > 0;
        HasRedo = _redoHistoryStack.Count > 0;
    }

    private void Undo()
    {
        if (_historyStack.Count == 0) return;
        _isUndoingOrRedoing = true;
        var action = _historyStack.Pop();
        action.Undo();
        _redoHistoryStack.Push(action);
        _isUndoingOrRedoing = false;
        UpdateHistoryStatus();
    }

    private void Redo()
    {
        if (_redoHistoryStack.Count == 0) return;
        _isUndoingOrRedoing = true;
        var action = _redoHistoryStack.Pop();
        action.Redo();
        _historyStack.Push(action);
        _isUndoingOrRedoing = false;
        UpdateHistoryStatus();
    }

    private void PushUndoAction(IHistoryAction action)
    {
        if (_isUndoingOrRedoing) return;
        _historyStack.Push(action);
        _redoHistoryStack.Clear();
        UpdateHistoryStatus();
    }

    public void AddAnnotation(Annotation annotation)
    {
        PushUndoAction(new AnnotationHistoryAction(Annotations, annotation, true));
        Annotations.Add(annotation);
    }

    public void RemoveAnnotation(Annotation annotation)
    {
        PushUndoAction(new AnnotationHistoryAction(Annotations, annotation, false));
        Annotations.Remove(annotation);
    }

    private void ClearAnnotations()
    {
        if (Annotations.Count == 0) return;
        PushUndoAction(new ClearAnnotationsHistoryAction(Annotations));
        Annotations.Clear();
        UpdateHistoryStatus();
    }

    public void ToggleToolGroup(string group)
    {
        if (group == "Shapes")
        {
            if (IsShapeToolActive)
            {
                CurrentAnnotationTool = AnnotationType.None;
                IsDrawingMode = false;
            }
            else
            {
                CurrentAnnotationTool = AnnotationType.Rectangle;
                IsDrawingMode = true;
            }
        }
        else if (group == "Pen")
        {
            if (IsPenToolActive)
            {
                CurrentAnnotationTool = AnnotationType.None;
                IsDrawingMode = false;
            }
            else
            {
                CurrentAnnotationTool = AnnotationType.Pen;
                IsDrawingMode = true;
            }
        }
        else if (group == "Text")
        {
            if (IsTextToolActive)
            {
                CurrentAnnotationTool = AnnotationType.None;
                IsDrawingMode = false;
            }
            else
            {
                CurrentAnnotationTool = AnnotationType.Text;
                IsDrawingMode = true;
            }
        }
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
    public ReactiveCommand<Unit, Unit> ChangeLanguageCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleBoldCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleItalicCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; set; } = null!;
    public ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; set; } = null!;

    private void InitializeToolbarCommands()
    {
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

        ClearAnnotationsCommand = ReactiveCommand.Create(ClearAnnotations);
        
        ToggleToolGroupCommand = ReactiveCommand.Create<string>(ToggleToolGroup);
        
        SelectToolCommand = ReactiveCommand.Create<AnnotationType>(t => {
            if (CurrentAnnotationTool == t)
            {
                CurrentAnnotationTool = AnnotationType.None;
                IsDrawingMode = false;
            }
            else
            {
                CurrentAnnotationTool = t;
                IsDrawingMode = true; 
            }
        });
        SelectToolCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        ChangeColorCommand = ReactiveCommand.Create<Color>(c => SelectedColor = c);
        ChangeColorCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        
        IncreaseThicknessCommand = ReactiveCommand.Create(() => { CurrentThickness = Math.Min(CurrentThickness + 1, 30); });
        DecreaseThicknessCommand = ReactiveCommand.Create(() => { CurrentThickness = Math.Max(CurrentThickness - 1, 1); });
        
        var canUndo = this.WhenAnyValue(x => x.HasUndo);
        var canRedo = this.WhenAnyValue(x => x.HasRedo);
        UndoCommand = ReactiveCommand.Create(Undo, canUndo);
        UndoCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));
        RedoCommand = ReactiveCommand.Create(Redo, canRedo);
        RedoCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Command error: {ex}"));

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
