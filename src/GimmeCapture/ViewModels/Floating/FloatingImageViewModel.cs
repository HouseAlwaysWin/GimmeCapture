using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Collections.Generic;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using System.Linq;
using System.Reactive.Linq;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.ViewModels.Shared;
using System;

namespace GimmeCapture.ViewModels.Floating;

public enum FloatingTool
{
    None,
    Selection,
    PointRemoval,
    InteractiveSelection
}

public partial class FloatingImageViewModel : FloatingWindowViewModelBase, IDrawingToolViewModel
{
    public bool ShowIconSettings => false;
    public ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; } = ReactiveCommand.Create(() => {});

    private Bitmap? _image;
    public Bitmap? Image
    {
        get => _image;
        set => this.RaiseAndSetIfChanged(ref _image, value);
    }
    
    public string CopyHotkey => _appSettingsService?.Settings.CopyHotkey ?? "Ctrl+C";
    public string PinHotkey => _appSettingsService?.Settings.PinHotkey ?? "F3";
    public string UndoHotkey => _appSettingsService?.Settings.UndoHotkey ?? "Ctrl+Z";
    public string RedoHotkey => _appSettingsService?.Settings.RedoHotkey ?? "Ctrl+Y";
    public string ClearHotkey => _appSettingsService?.Settings.ClearHotkey ?? "Delete";
    public string SaveHotkey => _appSettingsService?.Settings.SaveHotkey ?? "Ctrl+S";
    public string CloseHotkey => _appSettingsService?.Settings.CloseHotkey ?? "Escape";
    
    public string RectangleHotkey => _appSettingsService?.Settings.RectangleHotkey ?? "R";
    public string EllipseHotkey => _appSettingsService?.Settings.EllipseHotkey ?? "E";
    public string ArrowHotkey => _appSettingsService?.Settings.ArrowHotkey ?? "A";
    public string LineHotkey => _appSettingsService?.Settings.LineHotkey ?? "L";
    public string PenHotkey => _appSettingsService?.Settings.PenHotkey ?? "P";
    public string TextHotkey => _appSettingsService?.Settings.TextHotkey ?? "T";
    public string MosaicHotkey => _appSettingsService?.Settings.MosaicHotkey ?? "M";
    public string BlurHotkey => _appSettingsService?.Settings.BlurHotkey ?? "B";

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
    public string ToggleToolbarTooltip => $"{LocalizationService.Instance["ActionToolbar"]} ({_appSettingsService?.Settings.ToggleToolbarHotkey ?? "H"})";
    public string CloseTooltip => $"{LocalizationService.Instance["ActionClose"]} ({CloseHotkey})";
    public string SelectionTooltip => $"{LocalizationService.Instance["TipSelectionArea"]} (S)";
    public string CropTooltip => $"{LocalizationService.Instance["TipCrop"]} (C)";
    public string PinSelectionTooltip => $"{LocalizationService.Instance["TipPinSelection"]} (F3)";
    public string MagicWandTooltip => $"{LocalizationService.Instance["TipMagicWand"]} (W)";
    public string RemoveBackgroundTooltip => $"{LocalizationService.Instance["RemoveBackground"]} (Shift+R)";
    public string ConfirmRemovalTooltip => $"{LocalizationService.Instance["TipConfirmRemoval"]} (Enter)";
    public string CancelRemovalTooltip => $"{LocalizationService.Instance["Cancel"]} (Esc)";

    public System.Action? FocusWindowAction { get; set; }

    public IClipboardService ClipboardService => _clipboardService;
    public AIResourceService AIResourceService => _aiResourceService;
    public AIPathService AIPathService => _pathService;
    public AppSettingsService AppSettingsService => _appSettingsService;

    private readonly IClipboardService _clipboardService;
    private readonly AIResourceService _aiResourceService;
    private readonly AIPathService _pathService;
    private readonly AppSettingsService _appSettingsService;

    public override Avalonia.Thickness WindowPadding
    {
        get
        {
            double hPad = HidePinDecoration ? 10 : System.Math.Max(10, WingWidth);
            double topPad = 45;
            double baseBottomPad = 15;
            double bottomPad = baseBottomPad;
            if (ShowToolbar) bottomPad += 45;
            
            return new Avalonia.Thickness(hPad, topPad, hPad, bottomPad);
        }
    }


    public FloatingImageViewModel(Bitmap image, double originalWidth, double originalHeight, Avalonia.Media.Color borderColor, double borderThickness, bool hideDecoration, bool hideBorder, IClipboardService clipboardService, AIResourceService aiResourceService, AppSettingsService appSettingsService, AIPathService pathService)
    {
        Image = image;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
        DisplayWidth = originalWidth;
        DisplayHeight = originalHeight;
        BorderColor = borderColor;
        BorderThickness = borderThickness;
        HidePinDecoration = hideDecoration;
        HidePinBorder = hideBorder;
        _clipboardService = clipboardService;
        _aiResourceService = aiResourceService;
        _pathService = pathService;
        _appSettingsService = appSettingsService;

        InitializeActionCommands();
        InitializeAnnotationCommands();
        InitializeToolbarCommands();
        InitializeAICommands();

        _canRemoveBackground = this.WhenAnyValue(x => x.IsProcessing)
            .Select(x => !x)
            .ToProperty(this, x => x.CanRemoveBackground);

        RemoveBackgroundCommand = ReactiveCommand.CreateFromTask(RemoveBackgroundAsync, this.WhenAnyValue(x => x.IsProcessing).Select(p => !p));
        RemoveBackgroundCommand.ThrownExceptions.Subscribe((System.Exception ex) => System.Diagnostics.Debug.WriteLine($"Pinned AI Error: {ex}"));

        CancelInteractiveCommand = ReactiveCommand.Create(() => 
        {
            IsPointRemovalMode = false;
        });
        
        ConfirmInteractiveCommand = ReactiveCommand.CreateFromTask(ConfirmInteractiveAsync, this.WhenAnyValue(x => x.InteractiveMask).Select(m => m != null));
    }

    public override void Dispose()
    {
        _sam2Service?.Dispose();
        _sam2Service = null;
    }
}
