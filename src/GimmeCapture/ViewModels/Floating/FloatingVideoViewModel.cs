using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ReactiveUI;
using System;
using System.Reactive;
using System.Threading.Tasks;
using GimmeCapture.Models;
using System.Linq;
using System.Reactive.Linq;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel : ViewModelBase, IDisposable, IDrawingToolViewModel
{
    public bool ShowIconSettings => false;
    public ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; } = ReactiveCommand.Create(() => {});

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
        set
        {
            this.RaiseAndSetIfChanged(ref _hidePinDecoration, value);
            this.RaisePropertyChanged(nameof(WindowPadding));
        }
    }

    private bool _hidePinBorder = false;
    public bool HidePinBorder
    {
        get => _hidePinBorder;
        set => this.RaiseAndSetIfChanged(ref _hidePinBorder, value);
    }

    private double _originalWidth;
    public double OriginalWidth
    {
        get => _originalWidth;
        set => this.RaiseAndSetIfChanged(ref _originalWidth, value);
    }

    private double _originalHeight;
    public double OriginalHeight
    {
        get => _originalHeight;
        set => this.RaiseAndSetIfChanged(ref _originalHeight, value);
    }

    // Hotkey Proxies
    public string CopyHotkey => _appSettingsService?.Settings.CopyHotkey ?? "Ctrl+C";
    public string PinHotkey => _appSettingsService?.Settings.PinHotkey ?? "F3";
    public string UndoHotkey => _appSettingsService?.Settings.UndoHotkey ?? "Ctrl+Z";
    public string RedoHotkey => _appSettingsService?.Settings.RedoHotkey ?? "Ctrl+Y";
    public string ClearHotkey => _appSettingsService?.Settings.ClearHotkey ?? "Delete";
    public string SaveHotkey => _appSettingsService?.Settings.SaveHotkey ?? "Ctrl+S";
    public string CloseHotkey => _appSettingsService?.Settings.CloseHotkey ?? "Escape";
    public string PlaybackHotkey => _appSettingsService?.Settings.TogglePlaybackHotkey ?? "Space";
    
    public string RectangleHotkey => _appSettingsService?.Settings.RectangleHotkey ?? "R";
    public string EllipseHotkey => _appSettingsService?.Settings.EllipseHotkey ?? "E";
    public string ArrowHotkey => _appSettingsService?.Settings.ArrowHotkey ?? "A";
    public string LineHotkey => _appSettingsService?.Settings.LineHotkey ?? "L";
    public string PenHotkey => _appSettingsService?.Settings.PenHotkey ?? "P";
    public string TextHotkey => _appSettingsService?.Settings.TextHotkey ?? "T";
    public string MosaicHotkey => _appSettingsService?.Settings.MosaicHotkey ?? "M";
    public string BlurHotkey => _appSettingsService?.Settings.BlurHotkey ?? "B";

    // Tooltip Hints
    public string UndoTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["Undo"]} ({UndoHotkey})";
    public string RedoTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["Redo"]} ({RedoHotkey})";
    public string ClearTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["Clear"]} ({ClearHotkey})";
    public string SaveTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipSave"]} ({SaveHotkey})";
    public string CopyTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipCopy"]} ({CopyHotkey})";
    public string PinTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipPin"]} ({PinHotkey})";
    public string RectangleTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipRectangle"]} ({RectangleHotkey})";
    public string EllipseTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipEllipse"]} ({EllipseHotkey})";
    public string ArrowTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipArrow"]} ({ArrowHotkey})";
    public string LineTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipLine"]} ({LineHotkey})";
    public string PenTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipPen"]} ({PenHotkey})";
    public string TextTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipText"]} ({TextHotkey})";
    public string MosaicTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipMosaic"]} ({MosaicHotkey})";
    public string BlurTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipBlur"]} ({BlurHotkey})";
    public string PlaybackTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["ActionPlayback"]} ({PlaybackHotkey})";
    public string ToggleToolbarTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["ActionToolbar"]} ({_appSettingsService?.Settings.ToggleToolbarHotkey ?? "H"})";
    public string CloseTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["ActionClose"]} ({CloseHotkey})";
    public string RepeatTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["ActionRepeat"]}";
    public string SelectionTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipSelectionArea"]} (S)";
    public string CropTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipCrop"]} (C)";
    public string PinSelectionTooltip => $"{GimmeCapture.Services.Core.LocalizationService.Instance["TipPinSelection"]} (F3)";

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
            this.RaisePropertyChanged(nameof(WindowPadding));
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
            double hPad = _hidePinDecoration ? 10 : System.Math.Max(10, WingWidth);
            double vPad = 25;
            
            // RESERVE space for floating toolbar if visible
            // Two rows: Toolbar Height(32*2) + Spacing(4) + Bottom Margin(10) = 78px
            double bottomPad = vPad;
            if (ShowToolbar) bottomPad += 78;
            
            return new Avalonia.Thickness(hPad, vPad, hPad, bottomPad);
        }
    }

    // Added properties for Resize and Toolbar logic
    private double _displayWidth;
    public double DisplayWidth
    {
        get => _displayWidth;
        set => this.RaiseAndSetIfChanged(ref _displayWidth, value);
    }

    private double _displayHeight;
    public double DisplayHeight
    {
        get => _displayHeight;
        set => this.RaiseAndSetIfChanged(ref _displayHeight, value);
    }

    public System.Action<Avalonia.PixelPoint, double, double, double, double>? RequestSetWindowRect { get; set; }
    public System.Action? FocusWindowAction { get; set; }

    // Dependencies
    private readonly GimmeCapture.Services.Abstractions.IClipboardService _clipboardService;
    public GimmeCapture.Services.Abstractions.IClipboardService ClipboardService => _clipboardService;
    private readonly Services.Core.AppSettingsService? _appSettingsService;

    public FloatingVideoViewModel(string videoPath, string ffmpegPath, int width, int height, double originalWidth, double originalHeight, Avalonia.Media.Color borderColor, double borderThickness, bool hideDecoration, bool hideBorder, GimmeCapture.Services.Abstractions.IClipboardService clipboardService, Services.Core.AppSettingsService? appSettingsService)
    {
        VideoPath = videoPath;
        _ffmpegPath = ffmpegPath;
        _width = (width / 2) * 2; // Ensure even for FFmpeg
        _height = (height / 2) * 2;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
        DisplayWidth = originalWidth;
        DisplayHeight = originalHeight;
        BorderColor = borderColor;
        BorderThickness = borderThickness;
        HidePinDecoration = hideDecoration;
        HidePinBorder = hideBorder;
        _clipboardService = clipboardService;
        _appSettingsService = appSettingsService;

        InitializeActionCommands();
        InitializeToolbarCommands();
        InitializeAnnotationCommands();
        InitializeMediaCommands(); // Media init last as it starts playback
    }

    public void Dispose()
    {
        _playCts?.Cancel();
        _playCts?.Dispose();
        VideoBitmap?.Dispose();
    }
}
