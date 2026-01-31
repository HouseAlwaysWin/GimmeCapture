using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;

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

    public SnipWindowViewModel()
    {
        _captureService = new Services.ScreenCaptureService();

        CopyCommand = ReactiveCommand.CreateFromTask(Copy);
        SaveCommand = ReactiveCommand.CreateFromTask(Save);
        PinCommand = ReactiveCommand.CreateFromTask(Pin);
        CloseCommand = ReactiveCommand.Create(Close);
    }

    private async Task Copy() 
    { 
        if (SelectionRect.Width > 0 && SelectionRect.Height > 0)
        {
            HideAction?.Invoke();
            await Task.Delay(200); // Wait for UI update

            try 
            {
                var bitmap = await _captureService.CaptureScreenAsync(SelectionRect);
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
                 var bitmap = await _captureService.CaptureScreenAsync(SelectionRect);
                 
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
                var skBitmap = await _captureService.CaptureScreenAsync(SelectionRect);
                
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
