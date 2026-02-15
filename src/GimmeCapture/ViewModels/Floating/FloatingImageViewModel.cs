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
    // Scale Commands are inherited from Base

    private Bitmap? _image;
    public Bitmap? Image
    {
        get => _image;
        set => this.RaiseAndSetIfChanged(ref _image, value);
    }
    
    // Tooltips (Hotkeys are now in Base or accessed via AppSettingsService directly or we can keep proxies if needed, 
    // but Base doesn't have the *Service* reference by default unless we pass it or make it protected. 
    // Let's keep the specific Tooltips here for now but use the Services which are available here.)
    
    public override string CopyHotkey => _appSettingsService?.Settings.CopyHotkey ?? base.CopyHotkey;
    public override string PinHotkey => _appSettingsService?.Settings.PinHotkey ?? base.PinHotkey;
    public override string UndoHotkey => _appSettingsService?.Settings.UndoHotkey ?? base.UndoHotkey;
    public override string RedoHotkey => _appSettingsService?.Settings.RedoHotkey ?? base.RedoHotkey;
    public override string ClearHotkey => _appSettingsService?.Settings.ClearHotkey ?? base.ClearHotkey;
    public override string SaveHotkey => _appSettingsService?.Settings.SaveHotkey ?? base.SaveHotkey;
    public override string CloseHotkey => _appSettingsService?.Settings.CloseHotkey ?? base.CloseHotkey;
    
    public override string RectangleHotkey => _appSettingsService?.Settings.RectangleHotkey ?? base.RectangleHotkey;
    public override string EllipseHotkey => _appSettingsService?.Settings.EllipseHotkey ?? base.EllipseHotkey;
    public override string ArrowHotkey => _appSettingsService?.Settings.ArrowHotkey ?? base.ArrowHotkey;
    public override string LineHotkey => _appSettingsService?.Settings.LineHotkey ?? base.LineHotkey;
    public override string PenHotkey => _appSettingsService?.Settings.PenHotkey ?? base.PenHotkey;
    public override string TextHotkey => _appSettingsService?.Settings.TextHotkey ?? base.TextHotkey;
    public override string MosaicHotkey => _appSettingsService?.Settings.MosaicHotkey ?? base.MosaicHotkey;
    public override string BlurHotkey => _appSettingsService?.Settings.BlurHotkey ?? base.BlurHotkey;

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



    public IClipboardService ClipboardService => _clipboardService;
    public AIResourceService AIResourceService => _aiResourceService;
    public AIPathService AIPathService => _pathService;
    public AppSettingsService AppSettingsService => _appSettingsService;

    private readonly IClipboardService _clipboardService;
    private readonly AIResourceService _aiResourceService;
    private readonly AIPathService _pathService;
    private readonly AppSettingsService _appSettingsService = null!;

    // Overrides
    public override FloatingTool CurrentTool
    {
        get => base.CurrentTool;
        set 
        {
            if (base.CurrentTool == value) return;
            System.Diagnostics.Debug.WriteLine($"FloatingVM: Tool changing: {base.CurrentTool} -> {value}");
            
            // Cleanup previous tool state
            if (base.CurrentTool == FloatingTool.PointRemoval)
            {
                IsInteractiveSelectionMode = false;
                InteractiveMask = null;
                _interactivePoints.Clear();
            }
            else if (base.CurrentTool == FloatingTool.Selection)
            {
                SelectionRect = new Avalonia.Rect();
            }

            if (value != FloatingTool.None)
            {
                CurrentAnnotationTool = AnnotationType.None;
            }

            base.CurrentTool = value;
            
            // Notify UI properties
            this.RaisePropertyChanged(nameof(IsSelectionMode));
            this.RaisePropertyChanged(nameof(IsPointRemovalMode));
            this.RaisePropertyChanged(nameof(IsAnyToolActive));
            
            // Initialization for new tool
            if (value == FloatingTool.PointRemoval)
            {
                _ = StartInteractiveRemovalAsync();
            }
        }
    }

    public override AnnotationType CurrentAnnotationTool
    {
        get => base.CurrentAnnotationTool;
        set 
        {
            if (base.CurrentAnnotationTool == value) return;
            
            if (value != AnnotationType.None)
            {
                CurrentTool = FloatingTool.None;
            }

            base.CurrentAnnotationTool = value;
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsTextToolActive));
            this.RaisePropertyChanged(nameof(IsPenToolActive));
            this.RaisePropertyChanged(nameof(IsAnyToolActive));
        }
    }

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

        // Apply Default Toolbar Visibility
        ShowToolbar = !(_appSettingsService.Settings.DefaultHideSnipToolbar);

        InitializeBaseCommands();
        InitializeAnnotationCommands();
        // InitializeToolbarCommands(); // Moved to Base
        InitializeActionCommands(); // Keep for specific commands
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
