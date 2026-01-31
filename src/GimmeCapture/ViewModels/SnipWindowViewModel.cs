using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading.Tasks;

namespace GimmeCapture.ViewModels;

public partial class SnipWindowViewModel : ViewModelBase
{
    public enum SnipState { Idle, Detecting, Selecting, Selected }

    [ObservableProperty]
    private SnipState _currentState = SnipState.Idle;

    [ObservableProperty]
    private Rect _selectionRect;

    [ObservableProperty]
    private Geometry _screenGeometry = Geometry.Parse("M0,0 L0,0 0,0 0,0 Z"); // Default empty

    [ObservableProperty]
    private GeometryGroup _maskGeometry = new GeometryGroup();

    private readonly Services.IScreenCaptureService _captureService;

    public SnipWindowViewModel()
    {
        _captureService = new Services.ScreenCaptureService();
    }

    [RelayCommand]
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

    [RelayCommand]
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

    [RelayCommand]
    private void Close() { CloseAction?.Invoke(); }
    
    public Action? CloseAction { get; set; }
    public Action? HideAction { get; set; }
    public Func<Task<string?>>? PickSaveFileAction { get; set; }
}
