using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Collections.Generic;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Desktop;
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
    
    private Bitmap? _image;
    public Bitmap? Image
    {
        get => _image;
        set => this.RaiseAndSetIfChanged(ref _image, value);
    }
    
    // AI / SAM2 Properties & State
    private SAM2Service? _sam2Service;
    private readonly List<(double X, double Y, bool IsPositive)> _interactivePoints = new();
    private bool _invertSelectionMode = false; // Shift+Click sets this to true
    
    // Clean mask without crosshairs for actual removal
    private byte[]? _cleanMaskBytes;

    private bool _isInteractiveSelectionMode;
    public bool IsInteractiveSelectionMode
    {
        get => _isInteractiveSelectionMode;
        set => this.RaiseAndSetIfChanged(ref _isInteractiveSelectionMode, value);
    }

    private Bitmap? _interactiveMask;
    public Bitmap? InteractiveMask
    {
        get => _interactiveMask;
        set => this.RaiseAndSetIfChanged(ref _interactiveMask, value);
    }

    // Only allow background removal if not processing.
    private readonly ObservableAsPropertyHelper<bool> _canRemoveBackground;
    public bool CanRemoveBackground => _canRemoveBackground.Value;

    // Pinned Translation Support
    private string? _pinnedText;
    public string? PinnedText
    {
        get => _pinnedText;
        set => this.RaiseAndSetIfChanged(ref _pinnedText, value);
    }

    private double _inferredFontSize = 12.0;
    public double InferredFontSize
    {
        get => _inferredFontSize;
        set => this.RaiseAndSetIfChanged(ref _inferredFontSize, value);
    }

    public double ScaledFontSize => InferredFontSize * (DisplayWidth / Math.Max(1, OriginalWidth));

    public ReactiveCommand<Unit, Unit> RemoveBackgroundCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ConfirmInteractiveCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CancelInteractiveCommand { get; private set; } = null!;

    // Tooltips 
    public override string CopyHotkey => _appSettingsService?.Settings.Snip.Copy ?? base.CopyHotkey;
    public override string PinHotkey => _appSettingsService?.Settings.Snip.Pin ?? base.PinHotkey;
    public override string UndoHotkey => _appSettingsService?.Settings.Snip.Undo ?? base.UndoHotkey;
    public override string RedoHotkey => _appSettingsService?.Settings.Snip.Redo ?? base.RedoHotkey;
    public override string ClearHotkey => _appSettingsService?.Settings.Snip.Clear ?? base.ClearHotkey;
    public override string SaveHotkey => _appSettingsService?.Settings.Snip.Save ?? base.SaveHotkey;
    public override string CloseHotkey => _appSettingsService?.Settings.Snip.Close ?? base.CloseHotkey;
    
    public override string RectangleHotkey => _appSettingsService?.Settings.Snip.Rectangle ?? base.RectangleHotkey;
    public override string EllipseHotkey => _appSettingsService?.Settings.Snip.Ellipse ?? base.EllipseHotkey;
    public override string ArrowHotkey => _appSettingsService?.Settings.Snip.Arrow ?? base.ArrowHotkey;
    public override string LineHotkey => _appSettingsService?.Settings.Snip.Line ?? base.LineHotkey;
    public override string PenHotkey => _appSettingsService?.Settings.Snip.Pen ?? base.PenHotkey;
    public override string TextHotkey => _appSettingsService?.Settings.Snip.Text ?? base.TextHotkey;
    public override string MosaicHotkey => _appSettingsService?.Settings.Snip.Mosaic ?? base.MosaicHotkey;
    public override string BlurHotkey => _appSettingsService?.Settings.Snip.Blur ?? base.BlurHotkey;

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
    public string ToggleToolbarTooltip => $"{LocalizationService.Instance["ActionToolbar"]} ({_appSettingsService?.Settings.Snip.Toolbar ?? "H"})";
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
    private readonly IWindowManager _windowManager;
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
            System.Console.WriteLine($"[FloatingVM] Tool Changing: {base.CurrentTool} -> {value}");
            
            // Cleanup previous tool state
            if (base.CurrentTool == FloatingTool.PointRemoval)
            {
                IsInteractiveSelectionMode = false;
                InteractiveMask = null;
                _cleanMaskBytes = null;
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
                System.Console.WriteLine("[FloatingVM] Starting Interactive Removal Async...");
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


    // Actions
    public System.Action<Bitmap, Avalonia.Rect, Avalonia.Media.Color, double, bool, bool, string?, double>? OpenPinWindowAction { get; set; }

    // Commands
    public ReactiveCommand<Unit, Unit> CopyCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CutCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> CropCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> PinSelectionCommand { get; private set; } = null!;

    // AI Interactive Mode
    public bool IsPointRemovalMode
    {
        get => CurrentTool == FloatingTool.PointRemoval;
        set 
        {
            System.Console.WriteLine($"[FloatingVM] IsPointRemovalMode set to {value}");
            if (value) CurrentTool = FloatingTool.PointRemoval;
            else if (CurrentTool == FloatingTool.PointRemoval) CurrentTool = FloatingTool.None;
            this.RaisePropertyChanged();
        }
    }

    public override bool IsAnyToolActive => base.IsAnyToolActive || IsPointRemovalMode;

    public FloatingImageViewModel(Bitmap image, double originalWidth, double originalHeight, Avalonia.Media.Color borderColor, double borderThickness, bool hideDecoration, bool hideBorder, IClipboardService clipboardService, AIResourceService aiResourceService, AppSettingsService appSettingsService, AIPathService pathService, string? pinnedText = null, double inferredFontSize = 12.0)
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
        PinnedText = pinnedText;
        InferredFontSize = inferredFontSize;
        _clipboardService = clipboardService;
        _windowManager = new AvaloniaWindowManager();
        _aiResourceService = aiResourceService;
        _pathService = pathService;
        _appSettingsService = appSettingsService;

        // Apply Default Toolbar Visibility
        ShowToolbar = !(_appSettingsService.Settings.DefaultHideSnipToolbar);

        InitializeBaseCommands();
        InitializeAnnotationCommands();
        // InitializeToolbarCommands(); // Moved to Base
        InitializeActionCommands();
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

        // CRITICAL: Invalidate SAM2 service when Image changes (e.g. Crop, Undo)
        this.WhenAnyValue(x => x.Image)
            .Subscribe(_ =>
            {
                _sam2Service?.Dispose();
                _sam2Service = null;
                ResetInteractivePoints();
            });
    }

    public override void Dispose()
    {
        _sam2Service?.Dispose();
        _sam2Service = null;
    }
}
