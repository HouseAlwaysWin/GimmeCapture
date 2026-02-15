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

public partial class FloatingVideoViewModel : FloatingWindowViewModelBase, IDrawingToolViewModel
{
    public bool ShowIconSettings => false;
    public ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; } = ReactiveCommand.Create(() => {});

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
    public string UndoTooltip => $"{LocalizationService.Instance["Undo"]} ({UndoHotkey})";
    public string RedoTooltip => $"{LocalizationService.Instance["Redo"]} ({RedoHotkey})";
    public string ClearTooltip => $"{LocalizationService.Instance["Clear"]} ({ClearHotkey})";
    public string SaveTooltip => $"{LocalizationService.Instance["TipSave"]} ({SaveHotkey})";
    public string CopyTooltip => $"{LocalizationService.Instance["TipCopy"]} ({CopyHotkey})";
    public string PinTooltip => $"{LocalizationService.Instance["TipPin"]} ({PinHotkey})";
    public string RectangleTooltip => $"{LocalizationService.Instance["TipRectangle"]} ({RectangleHotkey})";
    public string EllipseTooltip => $"{LocalizationService.Instance["TipEllipse"]} ({EllipseHotkey})";
    public string ArrowTooltip => $"{LocalizationService.Instance["TipArrow"]} ({ArrowHotkey})";
    public string LineTooltip => $"{LocalizationService.Instance["TipLine"]} ({LineHotkey})";
    public string PenTooltip => $"{LocalizationService.Instance["TipPen"]} ({PenHotkey})";
    public string TextTooltip => $"{LocalizationService.Instance["TipText"]} ({TextHotkey})";
    public string MosaicTooltip => $"{LocalizationService.Instance["TipMosaic"]} ({MosaicHotkey})";
    public string BlurTooltip => $"{LocalizationService.Instance["TipBlur"]} ({BlurHotkey})";
    public string PlaybackTooltip => $"{LocalizationService.Instance["ActionPlayback"]} ({PlaybackHotkey})";
    public string ToggleToolbarTooltip => $"{LocalizationService.Instance["ActionToolbar"]} ({_appSettingsService?.Settings.ToggleToolbarHotkey ?? "H"})";
    public string CloseTooltip => $"{LocalizationService.Instance["ActionClose"]} ({CloseHotkey})";
    public string RepeatTooltip => $"{LocalizationService.Instance["ActionRepeat"]}";
    public string SelectionTooltip => $"{LocalizationService.Instance["TipSelectionArea"]} (S)";
    public string CropTooltip => $"{LocalizationService.Instance["TipCrop"]} (C)";
    public string PinSelectionTooltip => $"{LocalizationService.Instance["TipPinSelection"]} (F3)";

    public override Avalonia.Thickness WindowPadding
    {
        get
        {
            // If decorations are hidden, we just need the standard margin (e.g. 10 for shadow/resize handles).
            // If they are visible, we need enough space for the wings (WingWidth).
            double hPad = HidePinDecoration ? 10 : System.Math.Max(10, WingWidth);
            double vPad = 25;
            
            // RESERVE space for floating toolbar if visible
            // Two rows: Toolbar Height(32*2) + Spacing(4) + Bottom Margin(10) = 78px
            double bottomPad = vPad;
            if (ShowToolbar) bottomPad += 78;
            
            return new Avalonia.Thickness(hPad, vPad, hPad, bottomPad);
        }
    }

    public System.Action<Avalonia.PixelPoint, double, double, double, double>? RequestSetWindowRect { get; set; }
    public System.Action? FocusWindowAction { get; set; }


    // Dependencies
    private readonly GimmeCapture.Services.Abstractions.IClipboardService _clipboardService;
    public GimmeCapture.Services.Abstractions.IClipboardService ClipboardService => _clipboardService;
    private readonly AppSettingsService? _appSettingsService;

    public FloatingVideoViewModel(string videoPath, string ffmpegPath, int width, int height, double originalWidth, double originalHeight, Avalonia.Media.Color borderColor, double borderThickness, bool hideDecoration, bool hideBorder, GimmeCapture.Services.Abstractions.IClipboardService clipboardService, AppSettingsService? appSettingsService)
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

    public override void Dispose()
    {
        var cts = _playCts;
        if (cts != null)
        {
            Task.Run(() => { try { cts.Cancel(); } catch { } });
        }
        _playCts?.Dispose();
        VideoBitmap?.Dispose();
    }
}


