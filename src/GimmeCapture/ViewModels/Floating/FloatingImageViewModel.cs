using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Platforms.Windows;

using System.Linq;
using System.Reactive.Linq;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.ViewModels.Shared;
using System;

namespace GimmeCapture.ViewModels.Floating;

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
    
    // Only allow background removal if not processing.
    // We could also check if already transparent, but that's harder to detect cheaply.
    private readonly ObservableAsPropertyHelper<bool> _canRemoveBackground;
    public bool CanRemoveBackground => _canRemoveBackground.Value;

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleToolbarCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveBackgroundCommand { get; }
    
    public System.Action? CloseAction { get; set; }
    // CopyAction removed in favor of IClipboardService
    public System.Func<Task>? SaveAction { get; set; }

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

    public FloatingImageViewModel(Bitmap image, Avalonia.Media.Color borderColor, double borderThickness, bool hideDecoration, bool hideBorder, IClipboardService clipboardService, AIResourceService aiResourceService)
    {
        Image = image;
        BorderColor = borderColor;
        BorderThickness = borderThickness;
        HidePinDecoration = hideDecoration;
        HidePinBorder = hideBorder;
        _clipboardService = clipboardService;
        _aiResourceService = aiResourceService;

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

        _canRemoveBackground = this.WhenAnyValue(x => x.IsProcessing)
            .Select(x => !x)
            .ToProperty(this, x => x.CanRemoveBackground);

        RemoveBackgroundCommand = ReactiveCommand.CreateFromTask(RemoveBackgroundAsync, this.WhenAnyValue(x => x.IsProcessing).Select(p => !p));
        RemoveBackgroundCommand.ThrownExceptions.Subscribe((System.Exception ex) => System.Diagnostics.Debug.WriteLine($"Pinned AI Error: {ex}"));
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
            var transparentBytes = await aiService.RemoveBackgroundAsync(imageBytes);

            // 3. Update Image
            using var tms = new System.IO.MemoryStream(transparentBytes);
            // Replace the current image with the new transparent one
            var newBitmap = new Bitmap(tms);
            
            // Dispose old image if possible/safe? 
            // Avalonia bitmaps are ref counted roughly, but explicit dispose is good practice if we own it.
            // But we bound it to UI. UI will release ref when binding updates.
            Image = newBitmap; 
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
}
