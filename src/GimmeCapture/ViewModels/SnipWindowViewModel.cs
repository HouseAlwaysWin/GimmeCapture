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

    // Annotation Properties
    public ObservableCollection<Annotation> Annotations { get; } = new();

    private AnnotationType _currentTool = AnnotationType.Rectangle; // Default tool or None
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

    public SnipWindowViewModel()
    {
        _captureService = new Services.ScreenCaptureService();

        CopyCommand = ReactiveCommand.CreateFromTask(Copy);
        SaveCommand = ReactiveCommand.CreateFromTask(Save);
        PinCommand = ReactiveCommand.CreateFromTask(Pin);
        CloseCommand = ReactiveCommand.Create(Close);
        UndoCommand = ReactiveCommand.Create(() => { if (Annotations.Count > 0) Annotations.RemoveAt(Annotations.Count - 1); });
        ClearCommand = ReactiveCommand.Create(() => Annotations.Clear());
        SelectToolCommand = ReactiveCommand.Create<AnnotationType>(t => {
            CurrentTool = t;
            IsDrawingMode = true; // Once a tool is selected, enter drawing mode
        });

        ChangeColorCommand = ReactiveCommand.Create<Color>(c => SelectedColor = c);
    }

    public ReactiveCommand<Color, Unit> ChangeColorCommand { get; }

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
                OpenPinWindowAction?.Invoke(avaloniaBitmap, SelectionRect);
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
    public Action<Bitmap, Rect>? OpenPinWindowAction { get; set; }
}
