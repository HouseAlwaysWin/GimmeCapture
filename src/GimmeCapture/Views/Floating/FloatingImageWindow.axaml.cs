using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Models;
using System;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using System.Reactive.Linq;
using Avalonia.Interactivity;

namespace GimmeCapture.Views.Floating;

public partial class FloatingImageWindow : FloatingWindowBase
{
    private bool _isAIPointing;

    public FloatingImageWindow()
    {
        InitializeComponent();
        
        // Base constructor handles Event Handler registration for Pointer/Tapped/Key/Context
        
        // Sync Position to ViewModel for edge detection
        PositionChanged += (s, e) => {
            if (DataContext is FloatingImageViewModel vm) 
            {
                vm.ScreenPosition = Position;
            }
        };

        // Sync Toolbar measured size to ViewModel
        var toolbar = this.FindControl<Border>("ToolbarBorder");
        if (toolbar != null)
        {
            toolbar.GetObservable(Visual.BoundsProperty).Subscribe(bounds => {
                if (DataContext is FloatingImageViewModel vm)
                {
                    vm.ToolbarWidth = bounds.Width;
                    vm.ToolbarHeight = bounds.Height;
                }
            });
        }
    }

    protected override Control? GetContentControl() => this.FindControl<Image>("PinnedImage");

    protected override Bitmap? GetContentSnapshot() => (DataContext as FloatingImageViewModel)?.Image;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FloatingImageViewModel vm)
        {
            // Specific Image VM Setup
            
            vm.OpenPinWindowAction ??= (bitmap, rect, color, thickness, runAI) =>
            {
                var newVm = new FloatingImageViewModel(bitmap, rect.Width, rect.Height, color, thickness, vm.HidePinDecoration, vm.HidePinBorder, 
                    vm.ClipboardService, vm.AIResourceService, vm.AppSettingsService, vm.AIPathService);
                
                newVm.WingScale = vm.WingScale;
                newVm.CornerIconScale = vm.CornerIconScale;
                
                var newWin = new FloatingImageWindow
                {
                    DataContext = newVm,
                    Position = new PixelPoint(Position.X + 40, Position.Y + 40)
                };
                
                newWin.Show();
                
                if (runAI)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                        newVm.RemoveBackgroundCommand.Execute().Subscribe();
                    });
                }
            };
            
            // Re-Bind VM Properties specific to ImageWindow if needed
            vm.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(FloatingImageViewModel.Image))
                {
                    SyncWindowSizeToContent();
                }
            };
        }
    }

    // Override OnPointerPressed to handle AI Interactions
    protected override void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 1. Let Base handle Resize, Buttons, Drawing, Selection
        base.OnPointerPressed(sender, e);
        
        if (e.Handled) return;
        if (DataContext is not FloatingImageViewModel vm) return;

        // 2. AI Interaction — skip if the click target is a Button/ToggleButton (e.g. Confirm/Cancel)
        var pProperties = e.GetCurrentPoint(this).Properties;
        if (pProperties.IsLeftButtonPressed && vm.IsPointRemovalMode && !vm.IsProcessing)
        {
            // Walk visual tree to check if click landed on a toolbar button
            var visualSource = e.Source as Avalonia.Visual;
            while (visualSource != null)
            {
                if (visualSource is Button || visualSource is ToggleButton)
                    return; // Let the button handle the click naturally
                visualSource = visualSource.GetVisualParent();
            }

            _isAIPointing = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Right Click AI Undo
        if (pProperties.IsRightButtonPressed && vm.IsPointRemovalMode)
        {
            vm.UndoLastInteractivePoint();
            e.Handled = true;
        }
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        // 1. Let Base handle Resize, Drawing, Selection release
        base.OnPointerReleased(e);
        
        // 2. AI Interaction Release
        if (_isAIPointing)
        {
            var imageControl = GetContentControl(); // PinnedImage
            if (imageControl != null && DataContext is FloatingImageViewModel vm && vm.Image != null)
            {
                var pos = e.GetPosition(imageControl);
                var renderedRect = GetImageRenderedRect(imageControl as Image);
                
                if (renderedRect.Contains(pos))
                {
                    var relativeX = pos.X - renderedRect.X;
                    var relativeY = pos.Y - renderedRect.Y;
                    var sourceSize = vm.Image.PixelSize;
                    var pixelX = relativeX * (sourceSize.Width / renderedRect.Width);
                    var pixelY = relativeY * (sourceSize.Height / renderedRect.Height);
                    bool isPositive = !e.KeyModifiers.HasFlag(KeyModifiers.Shift);

                    await vm.HandlePointClickAsync(pixelX, pixelY, isPositive);
                }
            }
            e.Pointer.Capture(null);
            _isAIPointing = false;
            e.Handled = true;
        }
    }

    private Rect GetImageRenderedRect(Image? img)
    {
        if (img == null || img.Source == null || img.Bounds.Width <= 0 || img.Bounds.Height <= 0)
            return new Rect();

        var viewSize = img.Bounds.Size;
        var sourceSize = img.Source.Size;

        double scale = Math.Min(viewSize.Width / sourceSize.Width, viewSize.Height / sourceSize.Height);
        double w = sourceSize.Width * scale;
        double h = sourceSize.Height * scale;
        double x = (viewSize.Width - w) / 2;
        double y = (viewSize.Height - h) / 2;

        return new Rect(x, y, w, h);
    }
    
    // Handlers specific to XAML events not covered by Base
    private void OnAIToolSelected(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        System.Console.WriteLine("[FloatingWindow] OnAIToolSelected Clicked");
        Avalonia.Threading.Dispatcher.UIThread.Post(() => {
            var aiToolsButton = this.FindControl<Button>("AIToolsButton");
            aiToolsButton?.Flyout?.Hide();
        });
    }
}
