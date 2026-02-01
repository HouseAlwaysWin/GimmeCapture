using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;
using System.ComponentModel;

using System.Collections.ObjectModel;
using GimmeCapture.Models;
using System.Linq;
using GimmeCapture.Services;

namespace GimmeCapture.ViewModels;

public class SnipWindowViewModel : ViewModelBase
{
    public enum SnipState { Idle, Detecting, Selecting, Selected }

    private SnipState _currentState = SnipState.Idle;
    public SnipState CurrentState
    {
        get => _currentState;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentState, value);
            if (value == SnipState.Selected && AutoActionMode > 0)
            {
                TriggerAutoAction();
            }
        }
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

    private Rect _selectionRect;
    public Rect SelectionRect
    {
        get => _selectionRect;
        set => this.RaiseAndSetIfChanged(ref _selectionRect, value);
    }

    private Geometry _screenGeometry = Geometry.Parse("M0,0 L0,0 0,0 0,0 Z"); // Default empty
    public Geometry ScreenGeometry
    {
        get => _screenGeometry;
        set => this.RaiseAndSetIfChanged(ref _screenGeometry, value);
    }

    private GeometryGroup _maskGeometry = new GeometryGroup();
    public GeometryGroup MaskGeometry
    {
        get => _maskGeometry;
        set => this.RaiseAndSetIfChanged(ref _maskGeometry, value);
    }

    private readonly Services.IScreenCaptureService _captureService;

    // Commands
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> PinCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }

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

    private AnnotationType _currentTool = AnnotationType.None; // Default to None, no tool selected initially
    public AnnotationType CurrentTool
    {
        get => _currentTool;
        set 
        {
            this.RaiseAndSetIfChanged(ref _currentTool, value);
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsLineToolActive));
        }
    }

    public bool IsShapeToolActive => CurrentTool == AnnotationType.Rectangle || CurrentTool == AnnotationType.Ellipse;
    public bool IsLineToolActive => CurrentTool == AnnotationType.Arrow || CurrentTool == AnnotationType.Line;

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

    private bool _isDrawingMode = false;
    public bool IsDrawingMode
    {
        get => _isDrawingMode;
        set => this.RaiseAndSetIfChanged(ref _isDrawingMode, value);
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

    private double _maskOpacity = 0.5;
    public double MaskOpacity
    {
        get => _maskOpacity;
        set => this.RaiseAndSetIfChanged(ref _maskOpacity, value);
    }

    public SnipWindowViewModel()
    {
        _captureService = new Services.ScreenCaptureService();

        CopyCommand = ReactiveCommand.CreateFromTask(Copy);
        SaveCommand = ReactiveCommand.CreateFromTask(Save);
        PinCommand = ReactiveCommand.CreateFromTask(Pin);
        CloseCommand = ReactiveCommand.Create(Close);
        UndoCommand = ReactiveCommand.Create(Undo);
        RedoCommand = ReactiveCommand.Create(Redo);
        ClearCommand = ReactiveCommand.Create(() => Annotations.Clear()); // Clear remains destructive for now

        // Monitor for new user actions to clear Redo stack
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

        ChangeColorCommand = ReactiveCommand.Create<Color>(c => SelectedColor = c);
        
        IncreaseThicknessCommand = ReactiveCommand.Create(() => { if (CurrentThickness < 20) CurrentThickness += 1; });
        DecreaseThicknessCommand = ReactiveCommand.Create(() => { if (CurrentThickness > 1) CurrentThickness -= 1; });
        
        IncreaseFontSizeCommand = ReactiveCommand.Create(() => { if (CurrentFontSize < 60) CurrentFontSize += 2; });
        DecreaseFontSizeCommand = ReactiveCommand.Create(() => { if (CurrentFontSize > 10) CurrentFontSize -= 2; });
        
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
            catch { /* Invalid hex */ }
        });

        ChangeLanguageCommand = ReactiveCommand.Create(() => LocalizationService.Instance.CycleLanguage());
        ToggleBoldCommand = ReactiveCommand.Create(() => IsBold = !IsBold);
        ToggleItalicCommand = ReactiveCommand.Create(() => IsItalic = !IsItalic);
    }

    public SnipWindowViewModel(Color borderColor, double thickness, double opacity) : this()
    {
        SelectionBorderColor = borderColor;
        SelectionBorderThickness = thickness;
        MaskOpacity = opacity;

        // Tool defaults set to match border settings initially
        SelectedColor = borderColor;
        CurrentThickness = thickness;
    }

    public ReactiveCommand<Color, Unit> ChangeColorCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseFontSizeCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyHexColorCommand { get; }
    public ReactiveCommand<Unit, Unit> ChangeLanguageCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleBoldCommand { get; }
    public ReactiveCommand<Unit, bool> ToggleItalicCommand { get; }

    public static class StaticData
    {
        public static Color[] ColorsList { get; } = new[]
        {
            Color.Parse("#D4AF37"), // Gold
            Color.Parse("#E0E0E0"), // Silver
            Color.Parse("#E60012")  // Red
        };
    }

    private async Task Copy() 
    { 
        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            HideAction?.Invoke();
            await Task.Delay(200); // Wait for UI update

            try 
            {
                var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, Annotations);
                await _captureService.CopyToClipboardAsync(bitmap);
            }
            finally
            {
                CloseAction?.Invoke();
            }
        }
    }

    private async Task Save() 
    { 
         if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
         {
             HideAction?.Invoke();
             await Task.Delay(200); // Wait for UI update

             try
             {
                 var bitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, Annotations);
                 
                 if (PickSaveFileAction != null)
                 {
                     var path = await PickSaveFileAction.Invoke();
                     if (!string.IsNullOrEmpty(path))
                     {
                        await _captureService.SaveToFileAsync(bitmap, path);
                        System.Diagnostics.Debug.WriteLine($"Saved to {path}");
                     }
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
                 CloseAction?.Invoke(); 
             }
         }
    }
    
    private async Task Pin()
    {
        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            HideAction?.Invoke();
            await Task.Delay(200); // Wait for UI update
            
            try
            {
                var skBitmap = await _captureService.CaptureScreenWithAnnotationsAsync(SelectionRect, Annotations);
                
                // Convert SKBitmap to Avalonia Bitmap
                using var image = SkiaSharp.SKImage.FromBitmap(skBitmap);
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var stream = new System.IO.MemoryStream();
                data.SaveTo(stream);
                stream.Position = 0;
                
                var avaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                
                // Open Floating Window
            OpenPinWindowAction?.Invoke(avaloniaBitmap, SelectionRect, SelectionBorderColor, SelectionBorderThickness);
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
    public System.Action<Bitmap, Rect, Color, double>? OpenPinWindowAction { get; set; }
}
