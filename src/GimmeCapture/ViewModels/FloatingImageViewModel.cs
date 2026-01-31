using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;

namespace GimmeCapture.ViewModels;

public class FloatingImageViewModel : ViewModelBase
{
    private Bitmap? _image;
    public Bitmap? Image
    {
        get => _image;
        set => this.RaiseAndSetIfChanged(ref _image, value);
    }
    
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    
    // We might need to expose an Action to close the window from VM
    public System.Action? CloseAction { get; set; }

    public FloatingImageViewModel(Bitmap image)
    {
        Image = image;
        CloseCommand = ReactiveCommand.Create(() => CloseAction?.Invoke());
    }
}
