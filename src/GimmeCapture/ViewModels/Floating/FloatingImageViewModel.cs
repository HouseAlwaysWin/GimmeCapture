using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Threading.Tasks;
using System.Collections.Generic;
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
        set => this.RaiseAndSetIfChanged(ref _showToolbar, value);
    }

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set => this.RaiseAndSetIfChanged(ref _isProcessing, value);
    }

    private string _processingText = "Processing...";
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
                _sam2Service?.Dispose();
                _sam2Service = null;
            }
            else if (_currentTool == FloatingTool.Selection)
            {
                SelectionRect = new Avalonia.Rect();
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
                // Clean up AI when exiting PointRemoval mode
                _sam2Service?.Dispose();
                _sam2Service = null;
                IsInteractiveSelectionMode = false;
            }
            CurrentTool = value ? FloatingTool.PointRemoval : (CurrentTool == FloatingTool.PointRemoval ? FloatingTool.None : CurrentTool);
        }
    }

    public bool IsAnyToolActive => CurrentTool != FloatingTool.None;

    private SAM2Service? _sam2Service;
    private readonly List<(double X, double Y, bool IsPositive)> _interactivePoints = new();
    private bool _invertSelectionMode = false; // Shift+Click sets this to true

    private async Task StartInteractiveRemovalAsync()
    {
        System.Diagnostics.Debug.WriteLine("FloatingVM: Starting Interactive Removal Init");
        // Must check mode under UI thread or safely
        if (CurrentTool != FloatingTool.PointRemoval)
        {
            System.Diagnostics.Debug.WriteLine("FloatingVM: Aborting init - CurrentTool is not PointRemoval");
            return;
        }

        if (!await EnsureAIResourcesAsync())
        {
             System.Diagnostics.Debug.WriteLine("FloatingVM: AI resources failed");
             CurrentTool = FloatingTool.None;
             return;
        }

        try
        {
            IsProcessing = true;
            ProcessingText = LocalizationService.Instance["ProcessingAI"] ?? "Initializing AI...";
            
            _sam2Service?.Dispose();
            _sam2Service = new SAM2Service(_aiResourceService, _appSettingsService);
            System.Diagnostics.Debug.WriteLine("FloatingVM: Initializing SAM2 Service...");
            await _sam2Service.InitializeAsync();

            if (CurrentTool != FloatingTool.PointRemoval) 
            {
                System.Diagnostics.Debug.WriteLine("FloatingVM: Mode changed during SAM init, aborting.");
                return;
            }

            byte[] imageBytes;
            using (var ms = new System.IO.MemoryStream())
            {
                Image?.Save(ms);
                imageBytes = ms.ToArray();
            }

            System.Diagnostics.Debug.WriteLine("FloatingVM: Setting image to SAM2...");
            await _sam2Service.SetImageAsync(imageBytes);
            
            // Reset points list
            _interactivePoints.Clear();
            IsInteractiveSelectionMode = true; 
            
            // Show model info for diagnostic
            if (_sam2Service != null)
            {
                var encIn = _sam2Service.ModelInfo.Split('\n').FirstOrDefault(l => l.Contains("Inputs")) ?? "Unknown";
                DiagnosticText = $"AI Ready [{_sam2Service.ModelVariantName}]: {encIn.Trim()}";
                System.Diagnostics.Debug.WriteLine(_sam2Service.ModelInfo);
            }
            System.Diagnostics.Debug.WriteLine("FloatingVM: Interactive Selection Ready");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FloatingVM: Failed to start interactive removal: {ex}");
            DiagnosticText = "AI Init Failed";
            CurrentTool = FloatingTool.None;
            
            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                 var dialogVm = new GothicDialogViewModel { 
                     Title = "AI Initialization Error", 
                     Message = "Failed to start AI: " + ex.Message 
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
        _sam2Service?.ResetMaskInput(); // NEW: Clear the hidden mask input in AI service
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
            _sam2Service?.ResetMaskInput();

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
        if (_sam2Service == null) return;
    
        DiagnosticText = "AI: Refining...";
        try
        {
            var maskBytes = await _sam2Service.GetMaskAsync(_interactivePoints);
            var iouInfo = _sam2Service.LastIouInfo;
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
    }

    public async Task HandlePointClickAsync(double x, double y, bool isPositive = true)
    {
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
        
        SelectionCommand = ReactiveCommand.Create(() => 
        {
            CurrentTool = CurrentTool == FloatingTool.Selection ? FloatingTool.None : FloatingTool.Selection;
        });
        
        SaveCommand = ReactiveCommand.CreateFromTask(async () => 
        {
             if (SaveAction != null) await SaveAction();
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

    private Stack<Bitmap> _undoStack = new Stack<Bitmap>();
    private Stack<Bitmap> _redoStack = new Stack<Bitmap>();

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

    public ReactiveCommand<Unit, Unit> UndoCommand { get; }
    public ReactiveCommand<Unit, Unit> RedoCommand { get; }

    private void PushUndoState()
    {
        if (Image == null) return;
        
        // We need to clone the current bitmap, otherwise we just push a reference to the one we are about to change
        // In Avalonia, Bitmap does not have a direct Clone(), but we can save to streams.
        // Or simpler: Since we create NEW bitmaps on every change (immutable style), we *might* be able to just push the current reference 
        // IF the change replaces the property with a NEW reference.
        // Let's assume operation replaces property.
        
        _undoStack.Push(Image);
        _redoStack.Clear();
        
        UpdateStackStatus();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;

        var current = Image;
        if (current != null) _redoStack.Push(current);

        var prev = _undoStack.Pop();
        // Set backing field directly or property? Property triggers change notification which is good, 
        // BUT we need to avoid pushing to undo stack again if we had auto-push logic (we don't, it's manual).
        Image = prev;
        
        UpdateStackStatus();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;

        var current = Image;
        if (current != null) _undoStack.Push(current);

        var next = _redoStack.Pop();
        Image = next;

        UpdateStackStatus();
    }

    private void UpdateStackStatus()
    {
        HasUndo = _undoStack.Count > 0;
        HasRedo = _redoStack.Count > 0;
    }

    private async Task RemoveBackgroundAsync()
    {
        if (Image == null) return;
        
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

        if (IsSelectionActive)
        {
            var selected = await GetSelectedBitmapAsync();
            if (selected != null)
            {
                await _clipboardService.CopyImageAsync(selected);
            }
        }
        else
        {
            await _clipboardService.CopyImageAsync(Image);
        }
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
            PushUndoState();
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
}
