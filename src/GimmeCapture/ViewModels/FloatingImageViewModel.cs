using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;

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

    private bool _hidePinDecoration = false;
    public bool HidePinDecoration
    {
        get => _hidePinDecoration;
        set => this.RaiseAndSetIfChanged(ref _hidePinDecoration, value);
    }

    private bool _hidePinBorder = false;
    public bool HidePinBorder
    {
        get => _hidePinBorder;
        set => this.RaiseAndSetIfChanged(ref _hidePinBorder, value);
    }

    private bool _showToolbar = false;
    public bool ShowToolbar
    {
        get => _showToolbar;
        set => this.RaiseAndSetIfChanged(ref _showToolbar, value);
    }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleToolbarCommand { get; }
    
    public System.Action? CloseAction { get; set; }
    // CopyAction removed in favor of IClipboardService
    public System.Func<Task>? SaveAction { get; set; }

    private readonly IClipboardService _clipboardService;

    private double _wingScale = 1.0;
    public double WingScale
    {
        get => _wingScale;
        set
        {
            this.RaiseAndSetIfChanged(ref _wingScale, value);
            this.RaisePropertyChanged(nameof(WingWidth));
            this.RaisePropertyChanged(nameof(WingHeight));
            this.RaisePropertyChanged(nameof(LeftWingMargin));
            this.RaisePropertyChanged(nameof(RightWingMargin));
        }
    }

    private double _cornerIconScale = 1.0;
    public double CornerIconScale
    {
        get => _cornerIconScale;
        set
        {
            this.RaiseAndSetIfChanged(ref _cornerIconScale, value);
            this.RaisePropertyChanged(nameof(SelectionIconSize));
        }
    }
    
    // Derived properties for UI binding
    public double WingWidth => 100 * WingScale;
    public double WingHeight => 60 * WingScale;
    public double SelectionIconSize => 22 * CornerIconScale;
    public Avalonia.Thickness LeftWingMargin => new Avalonia.Thickness(-WingWidth, 0, 0, 0);
    public Avalonia.Thickness RightWingMargin => new Avalonia.Thickness(0, 0, -WingWidth, 0);

    public Avalonia.Thickness WindowPadding
    {
        get
        {
            // If decorations are hidden, we just need the standard margin (e.g. 10 for shadow/resize handles).
            // If they are visible, we need enough space for the wings (WingWidth).
            // We use Math.Max(10, WingWidth) to be safe, though WingWidth is usually ~100.
            double hPad = _hidePinDecoration ? 10 : System.Math.Max(10, WingWidth);
            double vPad = 10;
            return new Avalonia.Thickness(hPad, vPad, hPad, vPad);
        }
    }

    public FloatingImageViewModel(Bitmap image, Avalonia.Media.Color borderColor, double borderThickness, bool hideDecoration, bool hideBorder, IClipboardService clipboardService)
    {
        Image = image;
        BorderColor = borderColor;
        BorderThickness = borderThickness;
        HidePinDecoration = hideDecoration;
        HidePinBorder = hideBorder;
        _clipboardService = clipboardService;

        CloseCommand = ReactiveCommand.Create(() => CloseAction?.Invoke());
        ToggleToolbarCommand = ReactiveCommand.Create(() => { ShowToolbar = !ShowToolbar; });
        
        CopyCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (Image != null)
            {
                await _clipboardService.CopyImageAsync(Image);
            }
        });
        
        SaveCommand = ReactiveCommand.CreateFromTask(async () => 
        {
             if (SaveAction != null) await SaveAction();
        });
    }
}
