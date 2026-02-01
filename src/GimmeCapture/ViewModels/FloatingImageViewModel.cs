using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Threading.Tasks;

namespace GimmeCapture.ViewModels;

public class FloatingImageViewModel : ViewModelBase
{
    private Bitmap? _image;
    public Bitmap? Image
    {
        get => _image;
        set => this.RaiseAndSetIfChanged(ref _image, value);
    }
    
    private Avalonia.Media.Color _borderColor = Avalonia.Media.Colors.Red;
    public Avalonia.Media.Color BorderColor
    {
        get => _borderColor;
        set => this.RaiseAndSetIfChanged(ref _borderColor, value);
    }

    private double _borderThickness = 2.0;
    public double BorderThickness
    {
        get => _borderThickness;
        set => this.RaiseAndSetIfChanged(ref _borderThickness, value);
    }

    private bool _showPinDecoration = true;
    public bool ShowPinDecoration
    {
        get => _showPinDecoration;
        set => this.RaiseAndSetIfChanged(ref _showPinDecoration, value);
    }

    private bool _hidePinBorder = false;
    public bool HidePinBorder
    {
        get => _hidePinBorder;
        set => this.RaiseAndSetIfChanged(ref _hidePinBorder, value);
    }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    
    public System.Action? CloseAction { get; set; }
    public System.Func<Task>? CopyAction { get; set; }
    public System.Func<Task>? SaveAction { get; set; }

    public FloatingImageViewModel(Bitmap image, Avalonia.Media.Color borderColor, double borderThickness, bool showDecoration, bool hideBorder)
    {
        Image = image;
        BorderColor = borderColor;
        BorderThickness = borderThickness;
        ShowPinDecoration = showDecoration;
        HidePinBorder = hideBorder;

        CloseCommand = ReactiveCommand.Create(() => CloseAction?.Invoke());
        
        CopyCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (CopyAction != null) await CopyAction();
        });
        
        SaveCommand = ReactiveCommand.CreateFromTask(async () => 
        {
             if (SaveAction != null) await SaveAction();
        });
    }
}
