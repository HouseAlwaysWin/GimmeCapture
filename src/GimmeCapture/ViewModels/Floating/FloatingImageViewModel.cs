using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.ViewModels.Shared;
using System;
using SkiaSharp;

namespace GimmeCapture.ViewModels.Floating;

public enum FloatingTool
{
    None,
    Selection,
    PointRemoval,
    InteractiveSelection
}

public class FloatingImageViewModel : ViewModelBase, IDisposable, IDrawingToolViewModel
{
    public bool ShowIconSettings => false;
    public ReactiveCommand<Unit, Unit> IncreaseCornerIconScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseCornerIconScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> IncreaseWingScaleCommand { get; } = ReactiveCommand.Create(() => {});
    public ReactiveCommand<Unit, Unit> DecreaseWingScaleCommand { get; } = ReactiveCommand.Create(() => {});
    
    // Commands regarding Font Size (needed for interface even if we use Slider, actually I removed them from interface? No I kept them?)
    // Wait, I updated DrawingToolbar to use Slider, so I don't need Increase/DecreaseFontSizeCommand in interface?
    // I KEPT them in interface in the previous step (Step 2552 failed, but I re-applied/modified).
    // Let me check if they are in the interface.
    // Yes, Step 2603 (this step) added them back?
    // "ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; }"
    // So I MUST implement them.
    public ReactiveCommand<Unit, Unit> IncreaseFontSizeCommand { get; } 
    public ReactiveCommand<Unit, Unit> DecreaseFontSizeCommand { get; }

    public System.Collections.Generic.IEnumerable<Avalonia.Media.Color> PresetColors => GimmeCapture.ViewModels.Main.SnipWindowViewModel.StaticData.ColorsList;
    public ReactiveCommand<Avalonia.Media.Color, Unit> ChangeColorCommand { get; }
    public ReactiveCommand<Unit, Unit> IncreaseThicknessCommand { get; }
    public ReactiveCommand<Unit, Unit> DecreaseThicknessCommand { get; }



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

    private bool _showToolbar = false;
    public bool ShowToolbar
    {
        get => _showToolbar;
        set
        {
            this.RaiseAndSetIfChanged(ref _showToolbar, value);
            this.RaisePropertyChanged(nameof(WindowPadding));
        }
    }


    // Hotkey Proxies
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
    public string ToggleToolbarTooltip => $"{LocalizationService.Instance["ActionToolbar"]} ({_appSettingsService?.Settings.ToggleToolbarHotkey ?? "H"})";
    public string CloseTooltip => $"{LocalizationService.Instance["ActionClose"]} ({CloseHotkey})";
    public string SelectionTooltip => $"{LocalizationService.Instance["TipSelectionArea"]} (S)";
    public string CropTooltip => $"{LocalizationService.Instance["TipCrop"]} (C)";
    public string PinSelectionTooltip => $"{LocalizationService.Instance["TipPinSelection"]} (F3)";
    public string MagicWandTooltip => $"{LocalizationService.Instance["TipMagicWand"]} (W)";
    public string RemoveBackgroundTooltip => $"{LocalizationService.Instance["RemoveBackground"]} (Shift+R)";
    public string ConfirmRemovalTooltip => $"{LocalizationService.Instance["TipConfirmRemoval"]} (Enter)";
    public string CancelRemovalTooltip => $"{LocalizationService.Instance["Cancel"]} (Esc)";

    private double _toolbarTop;
    public double ToolbarTop
    {
        get => _toolbarTop;
        set => this.RaiseAndSetIfChanged(ref _toolbarTop, value);
    }

    private double _toolbarLeft;
    public double ToolbarLeft
    {
        get => _toolbarLeft;
        set => this.RaiseAndSetIfChanged(ref _toolbarLeft, value);
    }

    private double _toolbarWidth;
    public double ToolbarWidth
    {
        get => _toolbarWidth;
        set 
        {
            this.RaiseAndSetIfChanged(ref _toolbarWidth, value);
            UpdateToolbarPosition();
        }
    }

    private double _toolbarHeight;
    public double ToolbarHeight
    {
        get => _toolbarHeight;
        set 
        {
            this.RaiseAndSetIfChanged(ref _toolbarHeight, value);
            UpdateToolbarPosition();
        }
    }

    private void UpdateToolbarPosition()
    {
        if (!ShowToolbar || ToolbarWidth <= 0 || ToolbarHeight <= 0) return;

        // Toolbar is relative to the window content
        // Default: Bottom, Centered
        double paddingLeft = WindowPadding.Left;
        double paddingTop = WindowPadding.Top;
        double paddingBottom = WindowPadding.Bottom;
        
        double left = (DisplayWidth + paddingLeft + WindowPadding.Right - ToolbarWidth) / 2;
        
        // POSITTION INSIDE THE PADDING AREA
        // Window is size = DisplayHeight + TopPadding + BottomPadding
        // We want it a few pixels above the bottom edge.
        // paddingBottom is usually 45 or 45+42.
        double top = DisplayHeight + paddingTop + 5; 

        // Verify with screen bounds to see if we should flip
        // We need the window's screen position. This is usually provided by the View to the ViewModel.
        if (ScreenPosition.HasValue)
        {
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var window = desktop?.Windows.FirstOrDefault(w => w.DataContext == this);
            if (window != null && window.Screens != null)
            {
                var screen = window.Screens.ScreenFromVisual(window) ?? window.Screens.Primary;
                if (screen != null)
                {
                    // Calculate toolbar's absolute bottom
                    // window.Position is in Physical pixels.
                    double scaling = screen.Scaling;
                    double absTop = window.Position.Y + (top * scaling);
                    double absBottom = absTop + (ToolbarHeight * scaling);

                    // If it goes beyond screen work area bottom, flip to top
                    if (absBottom > screen.WorkingArea.Bottom - (10 * scaling))
                    {
                        top = -ToolbarHeight - 5;
                    }
                }
            }
        }

        ToolbarLeft = left;
        ToolbarTop = top;
    }

    private Avalonia.PixelPoint? _screenPosition;
    public Avalonia.PixelPoint? ScreenPosition
    {
        get => _screenPosition;
        set 
        {
            this.RaiseAndSetIfChanged(ref _screenPosition, value);
            UpdateToolbarPosition();
        }
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set => this.RaiseAndSetIfChanged(ref _isProcessing, value);
    }

    private string _processingText = LocalizationService.Instance["StatusProcessing"];
    public string ProcessingText
    {
        get => _processingText;
        set => this.RaiseAndSetIfChanged(ref _processingText, value);
    }

    private FloatingTool _currentTool = FloatingTool.None;
    public FloatingTool CurrentTool
    {
        get => _currentTool;
        set 
        {
            if (_currentTool == value) return;
            System.Diagnostics.Debug.WriteLine($"FloatingVM: Tool changing: {_currentTool} -> {value}");
            
            // Cleanup previous tool state
            if (_currentTool == FloatingTool.PointRemoval)
            {
                IsInteractiveSelectionMode = false;
                InteractiveMask = null;
                _interactivePoints.Clear();
            }
            else if (_currentTool == FloatingTool.Selection)
            {
                SelectionRect = new Avalonia.Rect();
            }

            if (value != FloatingTool.None)
            {
                CurrentAnnotationTool = AnnotationType.None;
            }

            this.RaiseAndSetIfChanged(ref _currentTool, value);
            
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

    private AnnotationType _currentAnnotationTool = AnnotationType.None;
    public AnnotationType CurrentAnnotationTool
    {
        get => _currentAnnotationTool;
        set 
        {
            if (_currentAnnotationTool == value) return;
            
            if (value != AnnotationType.None)
            {
                CurrentTool = FloatingTool.None;
            }

            this.RaiseAndSetIfChanged(ref _currentAnnotationTool, value);
            this.RaisePropertyChanged(nameof(IsShapeToolActive));
            this.RaisePropertyChanged(nameof(IsTextToolActive));
            this.RaisePropertyChanged(nameof(IsPenToolActive));
            this.RaisePropertyChanged(nameof(IsAnyToolActive));
        }
    }

    public void ToggleToolGroup(string group)
    {
        if (group == "Shapes")
        {
            if (IsShapeToolActive)
            {
                CurrentAnnotationTool = AnnotationType.None;
            }
            else
            {
                CurrentAnnotationTool = AnnotationType.Rectangle;
            }
        }
        else if (group == "Pen")
        {
            if (IsPenToolActive)
            {
                CurrentAnnotationTool = AnnotationType.None;
            }
            else
            {
                CurrentAnnotationTool = AnnotationType.Pen;
            }
        }
        else if (group == "Text")
        {
            if (IsTextToolActive)
            {
                CurrentAnnotationTool = AnnotationType.None;
            }
            else
            {
                CurrentAnnotationTool = AnnotationType.Text;
            }
        }
    }

    public void SelectTool(AnnotationType type)
    {
        CurrentAnnotationTool = type;
    }

    public ObservableCollection<Annotation> Annotations { get; } = new();

    public bool IsShapeToolActive => CurrentAnnotationTool == AnnotationType.Rectangle || CurrentAnnotationTool == AnnotationType.Ellipse || CurrentAnnotationTool == AnnotationType.Arrow || CurrentAnnotationTool == AnnotationType.Line || CurrentAnnotationTool == AnnotationType.Mosaic || CurrentAnnotationTool == AnnotationType.Blur;
    public bool IsPenToolActive => CurrentAnnotationTool == AnnotationType.Pen;
    public bool IsTextToolActive => CurrentAnnotationTool == AnnotationType.Text;

    // Explicit interface implementation to resolve name clash


    private Avalonia.Media.Color _selectedColor = Avalonia.Media.Colors.Red;
    public Avalonia.Media.Color SelectedColor
    {
        get => _selectedColor;
        set => this.RaiseAndSetIfChanged(ref _selectedColor, value);
    }

    private double _currentThickness = 2.0;
    public double CurrentThickness
    {
        get => _currentThickness;
        set => this.RaiseAndSetIfChanged(ref _currentThickness, value);
    }

    private double _currentFontSize = 24.0;
    public double CurrentFontSize
    {
        get => _currentFontSize;
        set => this.RaiseAndSetIfChanged(ref _currentFontSize, value);
    }

    private bool _isBold;
    public bool IsBold
    {
        get => _isBold;
        set => this.RaiseAndSetIfChanged(ref _isBold, value);
    }

    private bool _isItalic;
    public bool IsItalic
    {
        get => _isItalic;
        set => this.RaiseAndSetIfChanged(ref _isItalic, value);
    }

    private Avalonia.Rect _selectionRect = new Avalonia.Rect();
    public Avalonia.Rect SelectionRect
    {
        get => _selectionRect;
        set 
        {
            this.RaiseAndSetIfChanged(ref _selectionRect, value);
            this.RaisePropertyChanged(nameof(IsSelectionActive));
        }
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

    public bool IsSelectionActive => SelectionRect.Width > 0 && SelectionRect.Height > 0;

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

    // Clean mask without crosshairs for actual removal
    private byte[]? _cleanMaskBytes;

    private string _diagnosticText = "Ready";
    public string DiagnosticText
    {
        get => _diagnosticText;
        set => this.RaiseAndSetIfChanged(ref _diagnosticText, value);
    }

    public System.Action? FocusWindowAction { get; set; }

    private bool _isEnteringText;
    public bool IsEnteringText
    {
        get => _isEnteringText;
        set => this.RaiseAndSetIfChanged(ref _isEnteringText, value);
    }

    private string _pendingText = string.Empty;
    public string PendingText
    {
        get => _pendingText;
        set => this.RaiseAndSetIfChanged(ref _pendingText, value);
    }

    private Avalonia.Point _textInputPosition;
    public Avalonia.Point TextInputPosition
    {
        get => _textInputPosition;
        set => this.RaiseAndSetIfChanged(ref _textInputPosition, value);
    }

    private string _currentFontFamily = "Arial";
    public string CurrentFontFamily
    {
        get => _currentFontFamily;
        set => this.RaiseAndSetIfChanged(ref _currentFontFamily, value);
    }

    public ObservableCollection<double> Thicknesses { get; } = new() { 1, 2, 4, 6, 8, 12, 16, 24 };

    public ObservableCollection<string> AvailableFonts { get; } = new ObservableCollection<string>
    {
        "Arial", "Segoe UI", "Consolas", "Times New Roman", "Comic Sans MS", "Microsoft JhengHei", "Meiryo"
    };

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set => this.RaiseAndSetIfChanged(ref _progressValue, value);
    }
    
    private bool _isIndeterminate = true;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => this.RaiseAndSetIfChanged(ref _isIndeterminate, value);
    }
    
    public bool IsSelectionMode
    {
        get => CurrentTool == FloatingTool.Selection;
        set => CurrentTool = value ? FloatingTool.Selection : (CurrentTool == FloatingTool.Selection ? FloatingTool.None : CurrentTool);
    }

    public bool IsPointRemovalMode
    {
        get => CurrentTool == FloatingTool.PointRemoval;
        set {
            if (CurrentTool == FloatingTool.PointRemoval && !value)
            {
                // We keep SAM2Service alive for the window lifetime for fast re-entry
                IsInteractiveSelectionMode = false;
            }
            CurrentTool = value ? FloatingTool.PointRemoval : (CurrentTool == FloatingTool.PointRemoval ? FloatingTool.None : CurrentTool);
        }
    }

    public bool IsAnyToolActive => CurrentTool != FloatingTool.None || CurrentAnnotationTool != AnnotationType.None;

    private readonly List<(double X, double Y, bool IsPositive)> _interactivePoints = new();
    private bool _invertSelectionMode = false; // Shift+Click sets this to true

    private async Task StartInteractiveRemovalAsync()
    {
        if (CurrentTool != FloatingTool.PointRemoval) return;

        // Check if AI is enabled
        if (!_appSettingsService.Settings.EnableAI)
        {
            DiagnosticText = LocalizationService.Instance["AIDisabled"];
            CurrentTool = FloatingTool.None;
            
             Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 var dialogVm = new GothicDialogViewModel { 
                     Title = LocalizationService.Instance["AIDisabledTitle"], 
                     Message = LocalizationService.Instance["AIDisabledMessage"] 
                 };
                 var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };
                 
                 var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                 var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this) as Avalonia.Controls.Window;
                 
                  // Fallback: If owner specific to this VM not found, try any active FloatingImageWindow
                 if (owner == null)
                 {
                     owner = desktop?.Windows.OfType<GimmeCapture.Views.Floating.FloatingImageWindow>().FirstOrDefault(w => w.IsActive);
                 }
                 
                 // Final Fallback: Try Main Window or any active window
                 if (owner == null)
                 {
                     owner = desktop?.Windows.FirstOrDefault(w => w.IsActive) ?? desktop?.MainWindow;
                 }
                 
                 if (owner != null) 
                 {
                     dialog.ShowDialog<bool>(owner);
                 }
            });
            return;
        }

        var sam2 = await GetSAM2ServiceAsync();
        if (sam2 == null) return;

        try
        {
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["StatusInitializingAI"];
            
            // Image is already set by GetSAM2ServiceAsync using direct SKBitmap conversion
            
            // Reset points list
            _interactivePoints.Clear();
            IsInteractiveSelectionMode = true; 
            
            DiagnosticText = $"{LocalizationService.Instance["StatusReady"]} [{sam2.ModelVariantName}]";
            System.Diagnostics.Debug.WriteLine("FloatingVM: Interactive Selection Ready");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FloatingVM: Failed to start interactive removal: {ex}");
            DiagnosticText = LocalizationService.Instance["StatusError"]; // Or specify a new one
            CurrentTool = FloatingTool.None;
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 var dialogVm = new GothicDialogViewModel { 
                     Title = LocalizationService.Instance["AIInitErrorTitle"], 
                     Message = string.Format(LocalizationService.Instance["AIInitErrorMessage"], ex.Message)
                 };
                 var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };
                 var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                 var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this) as Avalonia.Controls.Window;
                 if (owner != null) dialog.ShowDialog<bool>(owner);
            });
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public void ResetInteractivePoints()
    {
        _interactivePoints.Clear();
        InteractiveMask = null;
        DiagnosticText = "AI: Points Reset";
        System.Diagnostics.Debug.WriteLine("FloatingVM: Resetting interactive points");
    }

    public async Task UndoLastPointAsync()
    {
        if (_interactivePoints.Count > 0)
        {
            _interactivePoints.RemoveAt(_interactivePoints.Count - 1);
            
            // CRITICAL: Reset the AI's mask feedback memory when undoing.
            // If the last result was a "bad" full-image mask, we don't want the AI to reuse it.

            if (_interactivePoints.Count == 0)
            {
                ResetInteractivePoints();
            }
            else
            {
                await RefineMaskAsync();
            }
        }
    }

    // Synchronous wrapper for right-click undo
    public void UndoLastInteractivePoint()
    {
        _ = UndoLastPointAsync();
    }

    private async Task RefineMaskAsync()
    {
        // Check Resources and download if needed (SAM2)
        if (!await EnsureAIResourcesAsync()) return;

        var sam2 = await GetSAM2ServiceAsync();
        if (sam2 == null) return;
    
        DiagnosticText = "AI: Refining...";
        try
        {
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["StatusProcessing"];
            
            var maskBytes = await sam2.GetMaskAsync(_interactivePoints);
            var iouInfo = sam2.LastIouInfo;
            DiagnosticText = $"AI: ({_interactivePoints.Count} pts) {iouInfo}";

            if (maskBytes != null && maskBytes.Length > 0)
            {
                // Store clean mask for actual removal (without crosshairs)
                _cleanMaskBytes = maskBytes;
                
                using var grayMask = SKBitmap.Decode(maskBytes);
                
                // CRITICAL FIX: Convert grayscale mask to RGBA with transparency
                // Color based on mode: Red (remove) vs Green (keep)
                SKColor overlayColor = _invertSelectionMode 
                    ? new SKColor(0, 255, 100, 150)   // Green for "Keep mode" (Shift+Click)
                    : new SKColor(255, 80, 80, 150);  // Red for "Remove mode" (Normal)
                    
                using var coloredMask = new SKBitmap(grayMask.Width, grayMask.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                for (int y = 0; y < grayMask.Height; y++)
                {
                    for (int x = 0; x < grayMask.Width; x++)
                    {
                        var grayVal = grayMask.GetPixel(x, y).Red; // Grayscale: R=G=B
                        if (grayVal > 127)
                        {
                            // Selected area with mode-specific color
                            coloredMask.SetPixel(x, y, overlayColor);
                        }
                        else
                        {
                            // Unselected area: Fully transparent
                            coloredMask.SetPixel(x, y, SKColors.Transparent);
                        }
                    }
                }
                
                using (var canvas = new SKCanvas(coloredMask))
                {
                    var posPaint = new SKPaint { Color = SKColors.LimeGreen, Style = SKPaintStyle.Fill, IsAntialias = true };
                    var negPaint = new SKPaint { Color = SKColors.Red, Style = SKPaintStyle.Fill, IsAntialias = true };
                    
                    // Scale points to match the mask bitmap size
                    float scaleX = (float)coloredMask.Width / (Image?.PixelSize.Width ?? 1);
                    float scaleY = (float)coloredMask.Height / (Image?.PixelSize.Height ?? 1);

                    foreach (var pt in _interactivePoints)
                    {
                        var px = (float)pt.X * scaleX;
                        var py = (float)pt.Y * scaleY;
                        
                        // Draw point circle
                        canvas.DrawCircle(px, py, 6, pt.IsPositive ? posPaint : negPaint);
                        
                        // DRAW CALIBRATION CROSSHAIR
                        using var crossPaint = new SKPaint { 
                            Color = pt.IsPositive ? SKColors.Lime : SKColors.DeepPink, 
                            StrokeWidth = 2, 
                            Style = SKPaintStyle.Stroke,
                            IsAntialias = true
                        };
                        canvas.DrawLine(px - 20, py, px + 20, py, crossPaint);
                        canvas.DrawLine(px, py - 20, px, py + 20, crossPaint);
                        
                        // Draw a tiny center dot
                        using var dotPaint = new SKPaint { Color = SKColors.Black.WithAlpha(180), Style = SKPaintStyle.Fill, IsAntialias = true };
                        canvas.DrawCircle(px, py, 1.5f, dotPaint);
                    }
                }

                using var finalMs = new System.IO.MemoryStream();
                coloredMask.Encode(finalMs, SKEncodedImageFormat.Png, 100);
                finalMs.Seek(0, System.IO.SeekOrigin.Begin);
                InteractiveMask = new Bitmap(finalMs);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FloatingVM: RefineMask Error: {ex}");
            DiagnosticText = $"Refine Error: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public async Task HandlePointClickAsync(double x, double y, bool isPositive = true)
    {
        if (IsProcessing) return;
        
        // LOG PHYSICAL PIXEL COORDINATES FOR USER VERIFICATION
        System.Diagnostics.Debug.WriteLine($"[AI DEBUG] Click Pixel: ({x:F0}, {y:F0}) Type: {(isPositive ? "Positive" : "Negative")}");
        
        var physicalX = x;
        var physicalY = y;

        if (_sam2Service == null || !IsInteractiveSelectionMode) return;

        try
        {
            // First point determines the mode:
            // - Positive (normal click) = Remove selected area
            // - Negative (Shift+click) = Keep selected area (invert result)
            if (_interactivePoints.Count == 0)
            {
                _invertSelectionMode = !isPositive;
                System.Diagnostics.Debug.WriteLine($"[AI MODE] First point. Invert mode = {_invertSelectionMode}");
            }
            
            // CRITICAL: Always send POSITIVE points to SAM2 (so it selects something)
            // The Shift key only affects how we interpret the FINAL result, not SAM2 input
            _interactivePoints.Add((physicalX, physicalY, true)); // Always true for SAM2

            await RefineMaskAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FloatingVM: Multi-point Error: {ex}");
            DiagnosticText = $"Click Error: {ex.Message}";
        }
    }

    private async Task<bool> ShowDownloadConfirmationAsync()
    {
        var msg = LocalizationService.Instance["AIDownloadConfirm"] ?? "Interactive AI Selection requires additional modules. Download now?";
        bool confirmed = false;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            // Find owner window (FloatingImageWindow)
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this);
            if (owner != null)
            {
                confirmed = await GimmeCapture.Views.Dialogs.UpdateDialog.ShowDialog(owner, msg, isUpdateAvailable: true);
            }
        });
        
        return confirmed;
    }

    public async Task<bool> EnsureAIResourcesAsync()
    {
        // 1. Check if already ready - Fast path
        var variant = _appSettingsService.Settings.SelectedSAM2Variant;
        if (_aiResourceService.IsAICoreReady() && _aiResourceService.IsSAM2Ready(variant)) return true;

        // 2. Check if already downloading (Background)
        var currentStatus = ResourceQueueService.Instance.GetStatus("AI");
        if (currentStatus == QueueItemStatus.Pending || currentStatus == QueueItemStatus.Downloading)
        {
             Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 var dialogVm = new GothicDialogViewModel { 
                     Title = "Download in Progress", 
                     Message = LocalizationService.Instance["ComponentDownloadingProgress"] ?? "Downloading component..." 
                 };
                 var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };
                 var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                 var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this) as Avalonia.Controls.Window;
                 if (owner != null) dialog.ShowDialog<bool>(owner);
            });
            return false;
        }

        // 3. Not ready, Not downloading -> Ask for permission
        var confirmed = await ShowDownloadConfirmationAsync();
        if (!confirmed) return false;

        // 4. Start Download (Fire and Forget from UI perspective)
        _ = ResourceQueueService.Instance.EnqueueAsync("AI", async () =>
        {
             // Download Core and Selected Variant
             bool coreReady = await _aiResourceService.EnsureAICoreAsync();
             if (!coreReady) return false;
             
             var variant = _appSettingsService.Settings.SelectedSAM2Variant;
             return await _aiResourceService.EnsureSAM2Async(variant);
        });

        return false;
    }

    private async Task DownloadAIResourcesAsync()
    {
        if (await EnsureAIResourcesAsync())
        {
            CurrentTool = FloatingTool.PointRemoval;
            this.RaisePropertyChanged(nameof(IsPointRemovalMode));
        }
    }
    
    // Only allow background removal if not processing.
    // We could also check if already transparent, but that's harder to detect cheaply.
    private readonly ObservableAsPropertyHelper<bool> _canRemoveBackground;
    public bool CanRemoveBackground => _canRemoveBackground.Value;

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> CutCommand { get; }
    public ReactiveCommand<Unit, Unit> CropCommand { get; }
    public ReactiveCommand<Unit, Unit> PinSelectionCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleToolbarCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveBackgroundCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectionCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmInteractiveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelInteractiveCommand { get; }
    
    public ReactiveCommand<AnnotationType, Unit> SelectToolCommand { get; }
    public ReactiveCommand<string, Unit> ToggleToolGroupCommand { get; }

    public ReactiveCommand<Unit, Unit> ClearAnnotationsCommand { get; }
    
    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmTextEntryCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelTextEntryCommand { get; }
    
    public System.Action? CloseAction { get; set; }
    
    // Action to open a new pinned window, typically provided by the View/Window layer
    public System.Action<Bitmap, Avalonia.Rect, Avalonia.Media.Color, double, bool>? OpenPinWindowAction { get; set; }
    
    public System.Func<Task>? SaveAction { get; set; }

    public IClipboardService ClipboardService => _clipboardService;
    public AIResourceService AIResourceService => _aiResourceService;
    public AppSettingsService AppSettingsService => _appSettingsService;

    private readonly IClipboardService _clipboardService;
    private readonly AIResourceService _aiResourceService;
    private readonly AppSettingsService _appSettingsService;
    private SAM2Service? _sam2Service;

    private async Task<SAM2Service?> GetSAM2ServiceAsync()
    {
        if (_sam2Service != null && _sam2Service.ModelVariantName != "Unknown") return _sam2Service;

        _sam2Service = new SAM2Service(_aiResourceService, _appSettingsService);
        ProcessingText = LocalizationService.Instance["StatusInitializingAI"];
        IsProcessing = true;
        try
        {
            await _sam2Service.InitializeAsync();
             // Optimization: Pass current image to AI immediately after initialization
            var skImage = ImageToSkia(Image);
            if (skImage != null)
            {
                await _sam2Service.SetImageAsync(skImage);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AI] Init Failed: {ex.Message}");
            _sam2Service = null;
        }
        finally
        {
            IsProcessing = false;
        }
        return _sam2Service;
    }

    private SKBitmap? ImageToSkia(Bitmap? avaloniaBitmap)
    {
        if (avaloniaBitmap == null) return null;
        try 
        {
            using var ms = new System.IO.MemoryStream();
            avaloniaBitmap.Save(ms);
            ms.Seek(0, System.IO.SeekOrigin.Begin);
            return SKBitmap.Decode(ms);
        }
        catch { return null; }
    }

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
            // Increased top padding to accommodate AI diagnostic text (spaced above size display)
            double vPad = 45;
            
            // RESERVE space for floating toolbar if visible
            // Toolbar Height(32) + Bottom Margin(10) = 42px
            double bottomPad = vPad;
            if (ShowToolbar) bottomPad += 42;
            
            return new Avalonia.Thickness(hPad, vPad, hPad, bottomPad);
        }
    }


    public FloatingImageViewModel(Bitmap image, double originalWidth, double originalHeight, Avalonia.Media.Color borderColor, double borderThickness, bool hideDecoration, bool hideBorder, IClipboardService clipboardService, AIResourceService aiResourceService, AppSettingsService appSettingsService)
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
        _appSettingsService = appSettingsService;

        CloseCommand = ReactiveCommand.Create(() => CloseAction?.Invoke());

        ToggleToolbarCommand = ReactiveCommand.Create(() => { ShowToolbar = !ShowToolbar; });

        // shared drawing commands
        IncreaseFontSizeCommand = ReactiveCommand.Create(() => { CurrentFontSize = Math.Min(CurrentFontSize + 2, 72); });
        DecreaseFontSizeCommand = ReactiveCommand.Create(() => { CurrentFontSize = Math.Max(CurrentFontSize - 2, 8); });
        ChangeColorCommand = ReactiveCommand.Create<Avalonia.Media.Color>(c => SelectedColor = c);
        IncreaseThicknessCommand = ReactiveCommand.Create(() => { CurrentThickness = Math.Min(CurrentThickness + 1, 30); });
        DecreaseThicknessCommand = ReactiveCommand.Create(() => { CurrentThickness = Math.Max(CurrentThickness - 1, 1); });
        
        SelectionCommand = ReactiveCommand.Create(() => 
        {
            CurrentTool = CurrentTool == FloatingTool.Selection ? FloatingTool.None : FloatingTool.Selection;
        });
        
        SaveCommand = ReactiveCommand.CreateFromTask(async () => 
        {
             if (SaveAction != null)
             {
                 // Temporary swap of Image for flattened version if we have annotations
                 var originalImage = Image;
                 var flattened = Annotations.Any() ? await GetFlattenedBitmapAsync() : null;
                 
                 if (flattened != null)
                 {
                     Image = flattened;
                 }
                 
                 try 
                 {
                    await SaveAction();
                 }
                 finally
                 {
                     if (flattened != null)
                     {
                         Image = originalImage;
                         // flattened.Dispose(); // Image property change might have disposed it or UI holds ref? 
                         // To be safe, we let GC handle it or explicit dispose if we know no one else strictly needs it.
                     }
                 }
             }
        });

        CopyCommand = ReactiveCommand.CreateFromTask(CopyAsync);
        CutCommand = ReactiveCommand.CreateFromTask(CutAsync, this.WhenAnyValue(x => x.IsSelectionActive));
        CropCommand = ReactiveCommand.CreateFromTask(CropAsync, this.WhenAnyValue(x => x.IsSelectionActive));
        PinSelectionCommand = ReactiveCommand.CreateFromTask(PinSelectionAsync, this.WhenAnyValue(x => x.IsSelectionActive));

        _canRemoveBackground = this.WhenAnyValue(x => x.IsProcessing)
            .Select(x => !x)
            .ToProperty(this, x => x.CanRemoveBackground);

        RemoveBackgroundCommand = ReactiveCommand.CreateFromTask(RemoveBackgroundAsync, this.WhenAnyValue(x => x.IsProcessing).Select(p => !p));
        RemoveBackgroundCommand.ThrownExceptions.Subscribe((System.Exception ex) => System.Diagnostics.Debug.WriteLine($"Pinned AI Error: {ex}"));

        var canUndo = this.WhenAnyValue(x => x.HasUndo).ObserveOn(RxApp.MainThreadScheduler);
        UndoCommand = ReactiveCommand.Create(Undo, canUndo);

        var canRedo = this.WhenAnyValue(x => x.HasRedo).ObserveOn(RxApp.MainThreadScheduler);
        RedoCommand = ReactiveCommand.Create(Redo, canRedo);

        ConfirmInteractiveCommand = ReactiveCommand.CreateFromTask(ConfirmInteractiveAsync, this.WhenAnyValue(x => x.InteractiveMask).Select(m => m != null));
        CancelInteractiveCommand = ReactiveCommand.Create(() => 
        {
            IsPointRemovalMode = false;
        });

        ConfirmTextEntryCommand = ReactiveCommand.Create(() => 
        {
            if (!string.IsNullOrWhiteSpace(PendingText))
            {
                AddAnnotation(new Annotation
                {
                    Type = AnnotationType.Text,
                    StartPoint = TextInputPosition,
                    EndPoint = TextInputPosition,
                    Text = PendingText,
                    Color = SelectedColor,
                    FontSize = CurrentFontSize,
                    FontFamily = CurrentFontFamily,
                    IsBold = IsBold,
                    IsItalic = IsItalic
                });
            }
            IsEnteringText = false;
            PendingText = string.Empty;
            FocusWindowAction?.Invoke();
        });

        CancelTextEntryCommand = ReactiveCommand.Create(() => 
        {
            IsEnteringText = false;
            PendingText = string.Empty;
            FocusWindowAction?.Invoke();
        });

        SelectToolCommand = ReactiveCommand.Create<AnnotationType>(tool => 
        {
            var targetTool = CurrentAnnotationTool == tool ? AnnotationType.None : tool;
            if (targetTool != AnnotationType.None)
            {
                CurrentTool = FloatingTool.None;
                IsPointRemovalMode = false;
                IsInteractiveSelectionMode = false;
            }
            CurrentAnnotationTool = targetTool;
        });

        ToggleToolGroupCommand = ReactiveCommand.Create<string>(group => 
        {
             AnnotationType targetTool = AnnotationType.None;
             if (group == "Shapes")
             {
                 targetTool = IsShapeToolActive ? AnnotationType.None : AnnotationType.Rectangle;
             }
             else if (group == "Pen")
             {
                 targetTool = (CurrentAnnotationTool == AnnotationType.Pen) ? AnnotationType.None : AnnotationType.Pen;
             }
             else if (group == "Text")
             {
                 targetTool = IsTextToolActive ? AnnotationType.None : AnnotationType.Text;
             }

             if (targetTool != AnnotationType.None)
             {
                 CurrentTool = FloatingTool.None;
                 IsPointRemovalMode = false;
                 IsInteractiveSelectionMode = false;
             }
             CurrentAnnotationTool = targetTool;
        });

        ChangeColorCommand = ReactiveCommand.Create<Avalonia.Media.Color>(c => SelectedColor = c);
        ClearAnnotationsCommand = ReactiveCommand.Create(ClearAnnotations);
    }

    private async Task ConfirmInteractiveAsync()
    {
        if (Image == null || InteractiveMask == null) return;

        try
        {
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["ProcessingAI"] ?? "Applying Removal...";
            
            PushUndoState();

            // 1. Process with SkiaSharp in a background thread to prevent UI freeze
            var imageBytes = await Task.Run(() =>
            {
                using var originalMs = new System.IO.MemoryStream();
                Image.Save(originalMs);
                using var originalBmp = SKBitmap.Decode(originalMs.ToArray());

                // Use CLEAN mask without crosshairs!
                if (_cleanMaskBytes == null) return null!; // Return empty if no clean mask
                using var maskBmp = SKBitmap.Decode(_cleanMaskBytes);
                
                // RESIZE MASK TO MATCH ORIGINAL BITMAP EXACTLY with Nearest sampling to avoid blurring edges
                // This ensures pixel-perfect alignment with the physical image
                using var resizedMask = maskBmp.Resize(new SKImageInfo(originalBmp.Width, originalBmp.Height), new SKSamplingOptions(SKFilterMode.Nearest));

                using var resultBmp = new SKBitmap(originalBmp.Width, originalBmp.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                
                for (int y = 0; y < originalBmp.Height; y++)
                {
                    for (int x = 0; x < originalBmp.Width; x++)
                    {
                        var color = originalBmp.GetPixel(x, y);
                        var maskColor = resizedMask.GetPixel(x, y);
                        
                        // Apply mask based on mode:
                        // Normal mode: Selected = REMOVE, Unselected = KEEP
                        // Invert mode: Selected = KEEP, Unselected = REMOVE
                        var maskVal = maskColor.Red; // For Gray8, R=G=B=value
                        bool isSelected = maskVal > 127;
                        
                        // Invert the selection if in invert mode
                        if (_invertSelectionMode)
                        {
                            isSelected = !isSelected;
                        }
                        
                        byte alpha;
                        if (isSelected)
                        {
                            // This pixel is in the "remove" zone - make transparent
                            alpha = 0;
                        }
                        else
                        {
                            // This pixel is in the "keep" zone - preserve original
                            alpha = color.Alpha;
                        }
                        
                        resultBmp.SetPixel(x, y, new SKColor(color.Red, color.Green, color.Blue, alpha));
                    }
                }

                using var image = SKImage.FromBitmap(resultBmp);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                return data.ToArray();
            });

            using var resultMs = new System.IO.MemoryStream(imageBytes);
            Image = new Bitmap(resultMs);

            IsPointRemovalMode = false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to confirm interactive: {ex}");
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private Stack<IHistoryAction> _historyStack = new();
    private Stack<IHistoryAction> _redoHistoryStack = new();

    private bool _hasUndo;
    public bool HasUndo
    {
        get => _hasUndo;
        set => this.RaiseAndSetIfChanged(ref _hasUndo, value);
    }

    private bool _hasRedo;
    public bool HasRedo
    {
        get => _hasRedo;
        set => this.RaiseAndSetIfChanged(ref _hasRedo, value);
    }

    public bool CanUndo => HasUndo;
    public bool CanRedo => HasRedo;

    public Action<Avalonia.PixelPoint, double, double, double, double>? RequestSetWindowRect { get; set; }

    public void PushUndoAction(IHistoryAction action)
    {
        _historyStack.Push(action);
        _redoHistoryStack.Clear();
        UpdateHistoryStatus();
    }

    public void PushResizeAction(Avalonia.PixelPoint oldPos, double oldW, double oldH, double oldContentW, double oldContentH,
                                Avalonia.PixelPoint newPos, double newW, double newH, double newContentW, double newContentH)
    {
        if (oldPos == newPos && oldW == newW && oldH == newH && oldContentW == newContentW && oldContentH == newContentH) return;
        
        PushUndoAction(new WindowTransformHistoryAction(
            (pos, w, h, cw, ch) => {
                DisplayWidth = cw;
                DisplayHeight = ch;
                RequestSetWindowRect?.Invoke(pos, w, h, cw, ch);
            },
            oldPos, oldW, oldH, oldContentW, oldContentH,
            newPos, newW, newH, newContentW, newContentH));
    }

    private void PushUndoState()
    {
        if (Image == null) return;
        PushUndoAction(new BitmapHistoryAction(b => Image = b, Image, null));
    }

    private void Undo()
    {
        if (_historyStack.Count == 0) return;
        var action = _historyStack.Pop();
        
        if (action is BitmapHistoryAction bh && bh.NewBitmap == null)
        {
            // If the action didn't have a new bitmap, it means it was a "capture state" action.
            // We should update it with the CURRENT bitmap as the NEW state before undoing.
            var actionWithNew = new BitmapHistoryAction(bh.SetBitmapAction, bh.OldBitmap, Image);
            actionWithNew.Undo();
            _redoHistoryStack.Push(actionWithNew);
        }
        else
        {
            action.Undo();
            _redoHistoryStack.Push(action);
        }
        
        UpdateHistoryStatus();
    }

    private void Redo()
    {
        if (_redoHistoryStack.Count == 0) return;
        var action = _redoHistoryStack.Pop();
        action.Redo();
        _historyStack.Push(action);
        UpdateHistoryStatus();
    }

    private void UpdateHistoryStatus()
    {
        HasUndo = _historyStack.Count > 0;
        HasRedo = _redoHistoryStack.Count > 0;
    }

    public void AddAnnotation(Annotation annotation)
    {
        Annotations.Add(annotation);
        PushUndoAction(new AnnotationHistoryAction(Annotations, annotation, true));
    }

    private void ClearAnnotations()
    {
        if (Annotations.Count == 0) return;
        PushUndoAction(new ClearAnnotationsHistoryAction(Annotations));
        Annotations.Clear();
    }

    private async Task RemoveBackgroundAsync()
    {
        if (Image == null) return;
        
        // Check if AI is enabled
        if (!_appSettingsService.Settings.EnableAI)
        {
             Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 var dialogVm = new GothicDialogViewModel { Title = "AI Disabled", Message = "AI features are currently disabled in Settings." };
                 var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };
                 
                 var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                 var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this) as Avalonia.Controls.Window;
                 
                 // Fallback: If owner specific to this VM not found, try any active FloatingImageWindow
                 if (owner == null)
                 {
                     owner = desktop?.Windows.OfType<GimmeCapture.Views.Floating.FloatingImageWindow>().FirstOrDefault(w => w.IsActive);
                 }
                 
                 // Final Fallback: Try Main Window or any active window
                 if (owner == null)
                 {
                     owner = desktop?.Windows.FirstOrDefault(w => w.IsActive) ?? desktop?.MainWindow;
                 }
                 
                 if (owner != null) 
                 {
                     dialog.ShowDialog<bool>(owner);
                 }
                 else
                 {
                     // Absolute fallback if no window found (should differ happen)
                     System.Diagnostics.Debug.WriteLine("[Error] No window found to show AI Disabled dialog");
                 }
            });
            return;
        }
        
        // Check Resources and download if needed
        if (!await EnsureAIResourcesAsync()) return;

        try
        {
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["ProcessingAI"] ?? "Processing...";
            
            // Save state for Undo
            PushUndoState();

             // 1. Convert Avalonia Bitmap to Bytes
            byte[] imageBytes;
            using (var ms = new System.IO.MemoryStream())
            {
                // We need to save the current bitmap to stream
                Image.Save(ms);
                imageBytes = ms.ToArray();
            }

            // 2. Process
            using var aiService = new BackgroundRemovalService(_aiResourceService);
            
            // SelectionRect is in logical pixels (UI space). 
            // We need to scale it to physical image pixels for BackgroundRemovalService.
            Avalonia.Rect? scaledRect = null;
            if (IsSelectionActive)
            {
                // Must use current DisplayWidth/Height for scaling the UI selection to physical pixels
                var refW = DisplayWidth > 0 ? DisplayWidth : OriginalWidth;
                var refH = DisplayHeight > 0 ? DisplayHeight : OriginalHeight;
                var scaleX = (double)Image.PixelSize.Width / refW;
                var scaleY = (double)Image.PixelSize.Height / refH;
                scaledRect = new Avalonia.Rect(
                    SelectionRect.X * scaleX,
                    SelectionRect.Y * scaleY,
                    SelectionRect.Width * scaleX,
                    SelectionRect.Height * scaleY);
            }

            var transparentBytes = await aiService.RemoveBackgroundAsync(imageBytes, scaledRect);

            // 3. Update Image
            using var tms = new System.IO.MemoryStream(transparentBytes);
            // Replace the current image with the new transparent one
            var newBitmap = new Bitmap(tms);
            
            // Dispose old image if possible/safe? 
            // Avalonia bitmaps are ref counted roughly, but explicit dispose is good practice if we own it.
            // But we bound it to UI. UI will release ref when binding updates.
            Image = newBitmap; 
            
            // Clear selection after processing
            IsSelectionMode = false;
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AI Processing Failed: {ex}");
            // Show error dialog
             Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 var dialogVm = new GothicDialogViewModel { Title = "Error", Message = ex.Message };
                 var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };
                 var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
                 var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this) as Avalonia.Controls.Window;
                 if (owner != null) dialog.ShowDialog<bool>(owner);
            });
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task<Bitmap?> GetSelectedBitmapAsync()
    {
        if (Image == null || !IsSelectionActive) return null;

        return await Task.Run(() =>
        {
            try
            {
                using var ms = new System.IO.MemoryStream();
                Image.Save(ms);
                ms.Position = 0;

                using var original = SkiaSharp.SKBitmap.Decode(ms);
                if (original == null) return null;

                // Must use current DisplayWidth/Height for scaling the UI selection to pixels
                var refW = DisplayWidth > 0 ? DisplayWidth : OriginalWidth;
                var refH = DisplayHeight > 0 ? DisplayHeight : OriginalHeight;
                var scaleX = (double)Image.PixelSize.Width / refW;
                var scaleY = (double)Image.PixelSize.Height / refH;

                int x = (int)Math.Round(Math.Max(0, SelectionRect.X * scaleX));
                int y = (int)Math.Round(Math.Max(0, SelectionRect.Y * scaleY));
                int w = (int)Math.Round(Math.Min(original.Width - x, SelectionRect.Width * scaleX));
                int h = (int)Math.Round(Math.Min(original.Height - y, SelectionRect.Height * scaleY));

                if (w <= 0 || h <= 0) return null;

                var cropped = new SkiaSharp.SKBitmap(w, h);
                if (original.ExtractSubset(cropped, new SkiaSharp.SKRectI(x, y, x + w, y + h)))
                {
                    using var cms = new System.IO.MemoryStream();
                    using var image = SkiaSharp.SKImage.FromBitmap(cropped);
                    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    data.SaveTo(cms);
                    cms.Position = 0;
                    return new Bitmap(cms);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting selection: {ex}");
            }
            return null;
        });
    }

    private async Task CopyAsync()
    {
        if (Image == null) return;

        // Use flattened bitmap if annotations exist, otherwise base image
        var bitmapToCopy = Annotations.Any() ? await GetFlattenedBitmapAsync() : Image;
        if (bitmapToCopy == null) bitmapToCopy = Image;

        if (IsSelectionActive)
        {
             // If selection is active, we should crop the FLATTENED bitmap, not just the base image
             // But existing GetSelectedBitmapAsync uses Image.
             // We need a version that uses bitmapToCopy.
             // For simplicity, let's just use grid cropping on the flattened bitmap if we can,
             // or just copy the whole thing if selection is not supported on flattened yet.
             // Actually, `GetSelectedBitmapAsync` logic is complex (scaling). 
             // Let's defer selection copy with annotations for a sec, or implement it properly.
             
             // Strategy: 
             // 1. Get flattened bitmap (entire image + annotations)
             // 2. Crop it using the same logic as GetSelectedBitmapAsync but operating on the new bitmap.
             
             var selected = await GetSelectedBitmapFromAsync(bitmapToCopy);
             if (selected != null)
             {
                 await _clipboardService.CopyImageAsync(selected);
             }
        }
        else
        {
            await _clipboardService.CopyImageAsync(bitmapToCopy);
        }
    }

    private async Task<Bitmap?> GetFlattenedBitmapAsync()
    {
        if (Image == null) return null;
        
        return await Task.Run(() => 
        {
            try 
            {
                // 1. Save base image to stream to load into SKBitmap
                using var ms = new System.IO.MemoryStream();
                Image.Save(ms);
                ms.Position = 0;
                
                using var skBitmap = SkiaSharp.SKBitmap.Decode(ms);
                if (skBitmap == null) return null;
                
                // 2. Create a surface to draw on
                using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(skBitmap.Width, skBitmap.Height));
                using var canvas = surface.Canvas;
                
                // 3. Draw base image
                canvas.DrawBitmap(skBitmap, 0, 0);
                
                // 4. Draw Annotations
                // Need to map coordinates from Display (View) space to Image (Pixel) space
                var refW = DisplayWidth > 0 ? DisplayWidth : OriginalWidth;
                var refH = DisplayHeight > 0 ? DisplayHeight : OriginalHeight;
                var scaleX = (double)skBitmap.Width / refW;
                var scaleY = (double)skBitmap.Height / refH;
                
                foreach (var ann in Annotations)
                {
                    var paint = new SkiaSharp.SKPaint
                    {
                        Color = new SkiaSharp.SKColor(ann.Color.R, ann.Color.G, ann.Color.B, ann.Color.A),
                        StrokeWidth = (float)(ann.Thickness * scaleX), // Scale thickness too?
                        IsAntialias = true,
                        Style = SkiaSharp.SKPaintStyle.Stroke
                    };
                    
                    if (ann.Type == AnnotationType.Pen)
                    {
                        paint.StrokeCap = SkiaSharp.SKStrokeCap.Round;
                        paint.StrokeJoin = SkiaSharp.SKStrokeJoin.Round;
                    }

                    switch (ann.Type)
                    {
                        case AnnotationType.Rectangle:
                        case AnnotationType.Ellipse:
                            var rect = new SkiaSharp.SKRect(
                                (float)(Math.Min(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                                (float)(Math.Min(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY),
                                (float)(Math.Max(ann.StartPoint.X, ann.EndPoint.X) * scaleX),
                                (float)(Math.Max(ann.StartPoint.Y, ann.EndPoint.Y) * scaleY));
                            
                            if (ann.Type == AnnotationType.Rectangle)
                                canvas.DrawRect(rect, paint);
                            else
                                canvas.DrawOval(rect, paint);
                            break;
                            
                        case AnnotationType.Line:
                            canvas.DrawLine(
                                (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY),
                                (float)(ann.EndPoint.X * scaleX), (float)(ann.EndPoint.Y * scaleY),
                                paint);
                            break;
                            
                        case AnnotationType.Arrow:
                            // Draw Line
                            float x1 = (float)(ann.StartPoint.X * scaleX);
                            float y1 = (float)(ann.StartPoint.Y * scaleY);
                            float x2 = (float)(ann.EndPoint.X * scaleX);
                            float y2 = (float)(ann.EndPoint.Y * scaleY);
                            canvas.DrawLine(x1, y1, x2, y2, paint);
                            
                            // Draw Arrowhead (Simple approximation)
                            // Calculate angle
                            double angle = Math.Atan2(y2 - y1, x2 - x1);
                            double arrowLen = 15 * scaleX; 
                            double arrowAngle = Math.PI / 6;
                            
                            float ax1 = (float)(x2 - arrowLen * Math.Cos(angle - arrowAngle));
                            float ay1 = (float)(y2 - arrowLen * Math.Sin(angle - arrowAngle));
                            float ax2 = (float)(x2 - arrowLen * Math.Cos(angle + arrowAngle));
                            float ay2 = (float)(y2 - arrowLen * Math.Sin(angle + arrowAngle));
                            
                            var path = new SkiaSharp.SKPath();
                            path.MoveTo(x2, y2);
                            path.LineTo(ax1, ay1);
                            path.LineTo(ax2, ay2);
                            path.Close();
                            
                            paint.Style = SkiaSharp.SKPaintStyle.Fill;
                            canvas.DrawPath(path, paint);
                            break;
                         
                         case AnnotationType.Pen:
                             // Snapshot points to avoid concurrent modification issues and use DrawPoints
                             if (ann.Points.Count > 1)
                             {
                                 var points = ann.Points.Select(p => new SkiaSharp.SKPoint((float)(p.X * scaleX), (float)(p.Y * scaleY))).ToArray();
                                 if (points.Length > 1)
                                 {
                                     canvas.DrawPoints(SkiaSharp.SKPointMode.Polygon, points, paint);
                                 }
                             }
                             break;
                             
                         case AnnotationType.Text:
                             // Simplified text rendering
                             var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, (float)(ann.FontSize * scaleX));
                             var textPaint = new SkiaSharp.SKPaint
                             {
                                 Color = paint.Color,
                                 IsAntialias = true,
                             };
                             // Adjust for font family/weight if needed, keeping simple for now
                             // canvas.DrawText(ann.Text, (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY + ann.FontSize * scaleY), font, textPaint);
                             // DrawText(text, x, y, font, paint) 
                             canvas.DrawText(ann.Text, (float)(ann.StartPoint.X * scaleX), (float)(ann.StartPoint.Y * scaleY + ann.FontSize * scaleY), SkiaSharp.SKTextAlign.Left, font, textPaint);
                             break;
                    }
                }
                
                // 5. Export result
                using var image = surface.Snapshot();
                using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                using var resultMs = new System.IO.MemoryStream();
                data.SaveTo(resultMs);
                resultMs.Position = 0;
                
                return new Bitmap(resultMs);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error flattening bitmap: {ex}");
                return null;
            }
        });
    }

    private async Task<Bitmap?> GetSelectedBitmapFromAsync(Bitmap sourceBitmap)
    {
         if (sourceBitmap == null) return null;
         
         return await Task.Run(() =>
         {
             try
             {
                 using var ms = new System.IO.MemoryStream();
                 sourceBitmap.Save(ms);
                 ms.Position = 0;
                 using var original = SkiaSharp.SKBitmap.Decode(ms);
                 if (original == null) return null;

                var refW = DisplayWidth > 0 ? DisplayWidth : OriginalWidth;
                var refH = DisplayHeight > 0 ? DisplayHeight : OriginalHeight;
                var scaleX = (double)original.Width / refW; 
                // Note: using original.Width because sourceBitmap might be same size as Image, or different if we flattened. 
                // Actually GetFlattenedBitmap returns same size as original Image. 
                // So scale factors should be calculated against the Display dimensions which represent the full image.
                
                var scaleY = (double)original.Height / refH;

                int x = (int)Math.Round(Math.Max(0, SelectionRect.X * scaleX));
                int y = (int)Math.Round(Math.Max(0, SelectionRect.Y * scaleY));
                int w = (int)Math.Round(Math.Min(original.Width - x, SelectionRect.Width * scaleX));
                int h = (int)Math.Round(Math.Min(original.Height - y, SelectionRect.Height * scaleY));

                if (w <= 0 || h <= 0) return null;

                var cropped = new SkiaSharp.SKBitmap(w, h);
                if (original.ExtractSubset(cropped, new SkiaSharp.SKRectI(x, y, x + w, y + h)))
                {
                    using var cms = new System.IO.MemoryStream();
                    using var image = SkiaSharp.SKImage.FromBitmap(cropped);
                    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    data.SaveTo(cms);
                    cms.Position = 0;
                    return new Bitmap(cms);
                }
             }
             catch(Exception ex)
             {
                 System.Diagnostics.Debug.WriteLine($"Error extracting selection from bitmap: {ex}");
             }
             return null;
         });
    }



    private async Task CutAsync()
    {
        if (Image == null || !IsSelectionActive) return;

        // 1. Copy selection to clipboard
        var selected = await GetSelectedBitmapAsync();
        if (selected != null)
        {
            await _clipboardService.CopyImageAsync(selected);
        }

        // 2. Actually crop it (Cut behavior in pinned window = Crop + Copy)
        await CropAsync();
    }

    private async Task CropAsync()
    {
        if (Image == null || !IsSelectionActive) return;

        var cropped = await GetSelectedBitmapAsync();
        if (cropped != null)
        {
            var oldImage = Image;
            var newImage = cropped;
            PushUndoAction(new BitmapHistoryAction(b => Image = b, oldImage, newImage));

            Image = cropped;
            SelectionRect = new Avalonia.Rect();
            IsSelectionMode = false;
        }
    }

    private async Task PinSelectionAsync()
    {
        if (Image == null || !IsSelectionActive || OpenPinWindowAction == null) return;

        var selected = await GetSelectedBitmapAsync();
        if (selected != null)
        {
            // Position the new window relative to the current one
            // We use the absolute screen coordinates if we had them, but here we provide relative rect.
            // FloatingImageWindow implementation in SnipWindow.axaml.cs uses rect.X/Y for Position.
            // We'll simulate that by passing the selection rect, but we need to know the window's screen position.
            // For now, let's just use a default or let the UI layer handle it if it can.
            
            // Need a way to get window screen position from VM if possible, or just pass the offset.
            // Since we don't have screen pos here, we just pass the selected bitmap.
            // The UI layer (FloatingImageWindow.axaml.cs) will handle the actual spawn.
            
            // Fake rect just for size, UI layer will position near cursor or current window
            var rect = new Avalonia.Rect(0, 0, selected.Size.Width, selected.Size.Height);
            
            OpenPinWindowAction(selected, rect, BorderColor, BorderThickness, false);
            
            // Clear selection after pinning
            SelectionRect = new Avalonia.Rect();
            IsSelectionMode = false;
        }
    }
    public void Dispose()
    {
        _sam2Service?.Dispose();
        _sam2Service = null;
    }
}
