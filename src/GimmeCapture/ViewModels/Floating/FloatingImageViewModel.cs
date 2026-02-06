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
using GimmeCapture.ViewModels.Main;
using GimmeCapture.ViewModels.Shared;
using System;

namespace GimmeCapture.ViewModels.Floating;

public enum FloatingTool
{
    None,
    Selection
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
            this.RaiseAndSetIfChanged(ref _currentTool, value);
            this.RaisePropertyChanged(nameof(IsSelectionMode));
            if (value == FloatingTool.None)
            {
                SelectionRect = new Avalonia.Rect();
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
    
    public bool IsSelectionMode
    {
        get => CurrentTool == FloatingTool.Selection;
        set => CurrentTool = value ? FloatingTool.Selection : FloatingTool.None;
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
    
    public System.Action? CloseAction { get; set; }
    
    // Action to open a new pinned window, typically provided by the View/Window layer
    public System.Action<Bitmap, Avalonia.Rect, Avalonia.Media.Color, double, bool>? OpenPinWindowAction { get; set; }
    
    public System.Func<Task>? SaveAction { get; set; }

    public IClipboardService ClipboardService => _clipboardService;
    public AIResourceService AIResourceService => _aiResourceService;

    private readonly IClipboardService _clipboardService;
    private readonly AIResourceService _aiResourceService;

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

    public FloatingImageViewModel(Bitmap image, double originalWidth, double originalHeight, Avalonia.Media.Color borderColor, double borderThickness, bool hideDecoration, bool hideBorder, IClipboardService clipboardService, AIResourceService aiResourceService)
    {
        Image = image;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
        BorderColor = borderColor;
        BorderThickness = borderThickness;
        HidePinDecoration = hideDecoration;
        HidePinBorder = hideBorder;
        _clipboardService = clipboardService;
        _aiResourceService = aiResourceService;

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
        
        // Check Resources
        if (!_aiResourceService.AreResourcesReady())
        {
            var title = LocalizationService.Instance["AIDownloadTitle"];
            var prompt = LocalizationService.Instance["AIDownloadPrompt"];
            
            var dialogVm = new GothicDialogViewModel { Title = title, Message = prompt };
            var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };

            // Find owner window (this floating window)
            // Since we are inside ViewModel, we don't have direct ref to Window, 
            // but we can try to find active window or rely on UI layer passing it.
            // For simplicity, we can fallback to App.Current or try finding window by DataContext if possible.
            // A better way is using a DialogService, but for now we look for active windows.
            var desktop = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var owner = desktop?.Windows.FirstOrDefault(w => w.DataContext == this) as Avalonia.Controls.Window;
            
            bool result = false;
            if (owner != null)
            {
                 result = await dialog.ShowDialog<bool>(owner);
            }
            
            if (!result) return;
            
            // Background download
             _ = _aiResourceService.EnsureResourcesAsync().ContinueWith(t => {
                if (!t.Result && owner != null)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                         // Error handling similar to SnipWindow
                         /* ... error dialog ... */
                    });
                }
            });
            return;
        }

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
