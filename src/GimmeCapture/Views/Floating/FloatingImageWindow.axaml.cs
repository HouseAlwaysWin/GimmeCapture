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
using GimmeCapture.Services.Interop;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using System.Reactive.Linq;
using Avalonia.Interactivity;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace GimmeCapture.Views.Floating;

public partial class FloatingImageWindow : FloatingWindowBase
{
    private bool _isAIPointing;
    private IDisposable? _toolbarBoundsSubscription;
    private IDisposable? _mainBorderBoundsSubscription;
    private IDisposable? _windowBoundsSubscription;
    private FloatingImageViewModel? _boundViewModel;
    private PropertyChangedEventHandler? _boundViewModelPropertyChangedHandler;

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
            _toolbarBoundsSubscription = toolbar.GetObservable(Visual.BoundsProperty).Subscribe(bounds => {
                if (DataContext is FloatingImageViewModel vm)
                {
                    vm.ToolbarWidth = bounds.Width;
                    vm.ToolbarHeight = bounds.Height;
                }

                RefreshWindowRegion();
            });
        }

        var mainBorder = this.FindControl<Border>("MainBorder");
        if (mainBorder != null)
        {
            _mainBorderBoundsSubscription = mainBorder.GetObservable(Visual.BoundsProperty)
                .Subscribe(_ => RefreshWindowRegion());
        }

        _windowBoundsSubscription = this.GetObservable(BoundsProperty)
            .Subscribe(_ => RefreshWindowRegion());
    }

    protected override Control? GetContentControl() => this.FindControl<Image>("PinnedImage");

    protected override Bitmap? GetContentSnapshot() => (DataContext as FloatingImageViewModel)?.Image;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_boundViewModel != null && _boundViewModelPropertyChangedHandler != null)
        {
            _boundViewModel.PropertyChanged -= _boundViewModelPropertyChangedHandler;
            _boundViewModel = null;
            _boundViewModelPropertyChangedHandler = null;
        }

        if (DataContext is FloatingImageViewModel vm)
        {
            // Specific Image VM Setup
            vm.ConfirmDialogAction ??= async message =>
            {
                var owner = TopLevel.GetTopLevel(this) as Window ?? this;
                return await GimmeCapture.Views.Dialogs.UpdateDialog.ShowDialog(owner, message, isUpdateAvailable: true);
            };

            vm.ShowDialogAction ??= (title, message) =>
            {
                var owner = TopLevel.GetTopLevel(this) as Window ?? this;
                var dialogVm = new GimmeCapture.ViewModels.Shared.GothicDialogViewModel
                {
                    Title = title,
                    Message = message
                };
                var dialog = new GimmeCapture.Views.Shared.GothicDialog { DataContext = dialogVm };
                dialog.ShowDialog<bool>(owner);
            };
            
            vm.OpenPinWindowAction ??= (bitmap, rect, color, thickness, runAI, initialInteractive, pinnedText, inferredFontSize) =>
            {
                var newVm = new FloatingImageViewModel(bitmap, rect.Width, rect.Height, color, thickness, vm.HidePinDecoration, vm.HidePinBorder, 
                    vm.ClipboardService, vm.AIResourceService, vm.SAM2RuntimeService, vm.AppSettingsService, vm.AIPathService, pinnedText, inferredFontSize);
                
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

                if (initialInteractive)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        newVm.IsPointRemovalMode = true;
                    });
                }
            };
            
            // Re-Bind VM Properties specific to ImageWindow if needed
            _boundViewModelPropertyChangedHandler = (s, ev) =>
            {
                if (ev.PropertyName == nameof(FloatingImageViewModel.Image))
                {
                    SyncWindowSizeToContent();
                }

                if (ev.PropertyName is nameof(FloatingImageViewModel.Image)
                    or nameof(FloatingImageViewModel.ShowToolbar)
                    or nameof(FloatingImageViewModel.DisplayWidth)
                    or nameof(FloatingImageViewModel.DisplayHeight)
                    or nameof(FloatingImageViewModel.HidePinDecoration)
                    or nameof(FloatingImageViewModel.HidePinBorder)
                    or nameof(FloatingImageViewModel.PinnedText))
                {
                    RefreshWindowRegion();
                }
            };
            vm.PropertyChanged += _boundViewModelPropertyChangedHandler;
            _boundViewModel = vm;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RefreshWindowRegion();
    }

    protected override void OnClosed(EventArgs e)
    {
        _toolbarBoundsSubscription?.Dispose();
        _toolbarBoundsSubscription = null;
        _mainBorderBoundsSubscription?.Dispose();
        _mainBorderBoundsSubscription = null;
        _windowBoundsSubscription?.Dispose();
        _windowBoundsSubscription = null;
        if (_boundViewModel != null && _boundViewModelPropertyChangedHandler != null)
        {
            _boundViewModel.PropertyChanged -= _boundViewModelPropertyChangedHandler;
            _boundViewModel = null;
            _boundViewModelPropertyChangedHandler = null;
        }

        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            Win32Helpers.ClearWindowRegion(hwnd);
        }

        base.OnClosed(e);
    }

    private void RefreshWindowRegion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var opaqueRects = new List<Rect>();
        AddSubtreeOpaqueRect("MainBorder", 2, opaqueRects);
        AddSubtreeOpaqueRect("ToolbarBorder", 4, opaqueRects);

        if (opaqueRects.Count == 0)
        {
            Win32Helpers.ClearWindowRegion(hwnd);
            return;
        }

        opaqueRects.Add(new Rect(0, 0, 1, 1));
        Win32Helpers.SetDisjointOpaqueRegions(hwnd, opaqueRects, null);
    }

    private void AddSubtreeOpaqueRect(string controlName, double logicalPadding, List<Rect> dest)
    {
        if (this.FindControl<Control>(controlName) is not Control root
            || !root.IsVisible
            || root.Bounds.Width <= 0
            || root.Bounds.Height <= 0)
        {
            return;
        }

        Rect? union = null;

        static IEnumerable<Control> EnumerateControls(Control rootControl)
        {
            yield return rootControl;
            foreach (var child in rootControl.GetVisualDescendants().OfType<Control>())
            {
                yield return child;
            }
        }

        foreach (var control in EnumerateControls(root))
        {
            if (!control.IsVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
            {
                continue;
            }

            var topLeft = control.TranslatePoint(new Point(0, 0), this);
            if (!topLeft.HasValue)
            {
                continue;
            }

            var rect = new Rect(topLeft.Value.X, topLeft.Value.Y, control.Bounds.Width, control.Bounds.Height);
            union = union.HasValue ? union.Value.Union(rect) : rect;
        }

        if (!union.HasValue || union.Value.Width <= 0 || union.Value.Height <= 0)
        {
            return;
        }

        var padded = new Rect(
            Math.Max(0, union.Value.X - logicalPadding),
            Math.Max(0, union.Value.Y - logicalPadding),
            union.Value.Width + (logicalPadding * 2),
            union.Value.Height + (logicalPadding * 2));

        double scaling = RenderScaling;
        dest.Add(new Rect(
            padded.X * scaling,
            padded.Y * scaling,
            padded.Width * scaling,
            padded.Height * scaling));
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
                    bool isInverseSelection =
                        e.KeyModifiers.HasFlag(KeyModifiers.Control) ||
                        e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                    bool isPositive = !isInverseSelection;

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
