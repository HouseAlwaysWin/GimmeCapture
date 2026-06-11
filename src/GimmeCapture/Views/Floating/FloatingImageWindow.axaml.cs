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
using System.Buffers;
using System.Runtime.InteropServices;
using System.Collections.ObjectModel;
using Avalonia.Controls.Shapes;

namespace GimmeCapture.Views.Floating;

public partial class FloatingImageWindow : FloatingWindowBase
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    private const int GWLP_WNDPROC = -4;
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private bool _isAIPointing;
    private IDisposable? _toolbarBoundsSubscription;
    private IDisposable? _mainBorderBoundsSubscription;
    private IDisposable? _windowBoundsSubscription;
    private FloatingImageViewModel? _boundViewModel;
    private PropertyChangedEventHandler? _boundViewModelPropertyChangedHandler;
    private Bitmap? _cachedOpaqueBitmap;
    private PixelSize _cachedOpaqueBitmapSize;
    private Rect _cachedOpaqueRenderedRect;
    private List<Rect>? _cachedOpaqueImageRects;
    private readonly List<Rect> _interactiveHitTestRegions = new();
    private WndProcDelegate? _wndProcDelegate;
    private IntPtr _oldWndProc;

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
                    or nameof(FloatingImageViewModel.PinnedText)
                    or nameof(FloatingImageViewModel.ToolbarMargin)
                    or nameof(FloatingImageViewModel.IsToolbarFlipped))
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
        InitializeWin32HitTestHook();
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

        UninitializeWin32HitTestHook();
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
        _interactiveHitTestRegions.Clear();
        AddImageOpaqueRects(opaqueRects);
        AddMainBorderFrameRects(opaqueRects);
        AddDecorationOpaqueRects(opaqueRects);
        AddSubtreeOpaqueRect("ResizeHandlesLayer", 2, opaqueRects, includeInHitTest: true);
        AddSubtreeOpaqueRect("PinnedTextOverlay", 2, opaqueRects, includeInHitTest: true);
        AddSubtreeOpaqueRect("SelectionCanvas", 2, opaqueRects, includeInHitTest: true);
        AddSubtreeOpaqueRect("ToolbarBorder", 4, opaqueRects, includeInHitTest: true);

        if (opaqueRects.Count == 0)
        {
            Win32Helpers.ClearWindowRegion(hwnd);
            return;
        }

        opaqueRects.Add(new Rect(0, 0, 1, 1));
        Win32Helpers.SetDisjointOpaqueRegions(hwnd, opaqueRects, null);
    }

    private void AddImageOpaqueRects(List<Rect> dest)
    {
        if (this.FindControl<Image>("PinnedImage") is not Image imageControl
            || DataContext is not FloatingImageViewModel vm
            || vm.Image == null)
        {
            return;
        }

        var renderedRect = GetImageRenderedRect(imageControl);
        if (renderedRect.Width <= 0 || renderedRect.Height <= 0)
        {
            return;
        }

        var imageTopLeft = imageControl.TranslatePoint(new Point(0, 0), this);
        if (!imageTopLeft.HasValue)
        {
            return;
        }

        var imageWindowRect = new Rect(
            imageTopLeft.Value.X + renderedRect.X,
            imageTopLeft.Value.Y + renderedRect.Y,
            renderedRect.Width,
            renderedRect.Height);

        var bitmap = vm.Image;
        if (_cachedOpaqueImageRects == null
            || !ReferenceEquals(_cachedOpaqueBitmap, bitmap)
            || _cachedOpaqueBitmapSize != bitmap.PixelSize
            || !RectsClose(_cachedOpaqueRenderedRect, imageWindowRect))
        {
            _cachedOpaqueBitmap = bitmap;
            _cachedOpaqueBitmapSize = bitmap.PixelSize;
            _cachedOpaqueRenderedRect = imageWindowRect;
            _cachedOpaqueImageRects = BuildOpaqueImageRects(bitmap, imageWindowRect);
        }

        if (_cachedOpaqueImageRects == null)
        {
            return;
        }

        double scaling = RenderScaling;
        foreach (var rect in _cachedOpaqueImageRects)
        {
            dest.Add(new Rect(rect.X * scaling, rect.Y * scaling, rect.Width * scaling, rect.Height * scaling));
            _interactiveHitTestRegions.Add(rect);
        }
    }

    private void AddMainBorderFrameRects(List<Rect> dest)
    {
        if (this.FindControl<Border>("MainBorder") is not Border border
            || !border.IsVisible
            || border.Bounds.Width <= 0
            || border.Bounds.Height <= 0)
        {
            return;
        }

        var topLeft = border.TranslatePoint(new Point(0, 0), this);
        if (!topLeft.HasValue)
        {
            return;
        }

        double frameThickness = Math.Max(1, (DataContext as FloatingImageViewModel)?.BorderThickness ?? 1) + 1;
        AddFrameRects(new Rect(topLeft.Value.X, topLeft.Value.Y, border.Bounds.Width, border.Bounds.Height), frameThickness, dest, includeInHitTest: true);
    }

    private void AddDecorationOpaqueRects(List<Rect> dest)
    {
        if (this.FindControl<Control>("PinDecoration") is not Control decorationRoot
            || !decorationRoot.IsVisible)
        {
            return;
        }

        double scaling = RenderScaling;
        foreach (var rectShape in decorationRoot.GetVisualDescendants().OfType<Rectangle>())
        {
            if (!rectShape.IsVisible || rectShape.Bounds.Width <= 0 || rectShape.Bounds.Height <= 0)
            {
                continue;
            }

            var topLeft = rectShape.TranslatePoint(new Point(0, 0), this);
            if (!topLeft.HasValue)
            {
                continue;
            }

            var logicalRect = new Rect(topLeft.Value.X, topLeft.Value.Y, rectShape.Bounds.Width, rectShape.Bounds.Height);
            dest.Add(new Rect(logicalRect.X * scaling, logicalRect.Y * scaling, logicalRect.Width * scaling, logicalRect.Height * scaling));
        }
    }

    private void AddSubtreeOpaqueRect(string controlName, double logicalPadding, List<Rect> dest, bool includeInHitTest = false)
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
        var logicalRect = new Rect(
            padded.X,
            padded.Y,
            padded.Width,
            padded.Height);

        dest.Add(new Rect(
            padded.X * scaling,
            padded.Y * scaling,
            padded.Width * scaling,
            padded.Height * scaling));

        if (includeInHitTest)
        {
            _interactiveHitTestRegions.Add(logicalRect);
        }
    }

    private void AddFrameRects(Rect outer, double thickness, List<Rect> dest, bool includeInHitTest)
    {
        if (outer.Width <= 0 || outer.Height <= 0)
        {
            return;
        }

        double t = Math.Max(1, thickness);
        double scaling = RenderScaling;
        var rects = new[]
        {
            new Rect(outer.X, outer.Y, outer.Width, t),
            new Rect(outer.X, outer.Bottom - t, outer.Width, t),
            new Rect(outer.X, outer.Y + t, t, Math.Max(0, outer.Height - (t * 2))),
            new Rect(outer.Right - t, outer.Y + t, t, Math.Max(0, outer.Height - (t * 2)))
        };

        foreach (var rect in rects)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            dest.Add(new Rect(rect.X * scaling, rect.Y * scaling, rect.Width * scaling, rect.Height * scaling));
            if (includeInHitTest)
            {
                _interactiveHitTestRegions.Add(rect);
            }
        }
    }

    private static bool RectsClose(Rect a, Rect b)
    {
        return Math.Abs(a.X - b.X) < 0.5
            && Math.Abs(a.Y - b.Y) < 0.5
            && Math.Abs(a.Width - b.Width) < 0.5
            && Math.Abs(a.Height - b.Height) < 0.5;
    }

    private static List<Rect> BuildOpaqueImageRects(Bitmap bitmap, Rect renderedRect)
    {
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        const int bytesPerPixel = 4;
        int stride = width * bytesPerPixel;
        byte[] rented = ArrayPool<byte>.Shared.Rent(stride * height);
        GCHandle handle = default;

        try
        {
            handle = GCHandle.Alloc(rented, GCHandleType.Pinned);
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), stride * height, stride);

            double scaleX = renderedRect.Width / width;
            double scaleY = renderedRect.Height / height;
            var active = new Dictionary<(int Start, int Length), Rect>();
            var result = new List<Rect>();

            for (int y = 0; y < height; y++)
            {
                var next = new Dictionary<(int Start, int Length), Rect>();
                int rowOffset = y * stride;
                int x = 0;

                while (x < width)
                {
                    int alphaIndex = rowOffset + (x * bytesPerPixel) + 3;
                    if (rented[alphaIndex] <= 8)
                    {
                        x++;
                        continue;
                    }

                    int start = x;
                    x++;
                    while (x < width)
                    {
                        alphaIndex = rowOffset + (x * bytesPerPixel) + 3;
                        if (rented[alphaIndex] <= 8)
                        {
                            break;
                        }

                        x++;
                    }

                    int length = x - start;
                    var key = (start, length);
                    if (active.Remove(key, out var existing))
                    {
                        next[key] = new Rect(existing.X, existing.Y, existing.Width, existing.Height + scaleY);
                    }
                    else
                    {
                        next[key] = new Rect(
                            renderedRect.X + (start * scaleX),
                            renderedRect.Y + (y * scaleY),
                            Math.Max(scaleX, length * scaleX),
                            Math.Max(scaleY, scaleY));
                    }
                }

                foreach (var leftover in active.Values)
                {
                    result.Add(leftover);
                }

                active = next;
            }

            foreach (var rect in active.Values)
            {
                result.Add(rect);
            }

            return result;
        }
        finally
        {
            if (handle.IsAllocated)
            {
                handle.Free();
            }

            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void InitializeWin32HitTestHook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || _wndProcDelegate != null)
        {
            return;
        }

        _wndProcDelegate = new WndProcDelegate(WndProcHook);
        IntPtr ptr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);
        _oldWndProc = IntPtr.Size == 8
            ? SetWindowLongPtr64(hwnd, GWLP_WNDPROC, ptr)
            : SetWindowLongPtr32(hwnd, GWLP_WNDPROC, ptr);
    }

    private void UninitializeWin32HitTestHook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero || _wndProcDelegate == null || _oldWndProc == IntPtr.Zero)
        {
            _wndProcDelegate = null;
            _oldWndProc = IntPtr.Zero;
            return;
        }

        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hwnd, GWLP_WNDPROC, _oldWndProc);
        }
        else
        {
            SetWindowLongPtr32(hwnd, GWLP_WNDPROC, _oldWndProc);
        }

        _wndProcDelegate = null;
        _oldWndProc = IntPtr.Zero;
    }

    private IntPtr WndProcHook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCHITTEST && _interactiveHitTestRegions.Count > 0)
        {
            int lp = lParam.ToInt32();
            var pt = new POINT { X = (short)(lp & 0xFFFF), Y = (short)((lp >> 16) & 0xFFFF) };
            ScreenToClient(hWnd, ref pt);

            double scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
            var clientPoint = new Point(pt.X / scaling, pt.Y / scaling);
            bool hitInteractive = _interactiveHitTestRegions.Any(rect => rect.Contains(clientPoint));
            if (!hitInteractive)
            {
                return new IntPtr(HTTRANSPARENT);
            }
        }

        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
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
