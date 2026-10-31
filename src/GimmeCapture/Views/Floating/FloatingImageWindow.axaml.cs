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
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Interaction;
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
    private bool _isAIPointing;
    private IDisposable? _toolbarBoundsSubscription;
    private IDisposable? _subToolbarBoundsSubscription;
    private IDisposable? _mainBorderBoundsSubscription;
    private IDisposable? _windowBoundsSubscription;
    private FloatingImageViewModel? _boundViewModel;
    private PropertyChangedEventHandler? _boundViewModelPropertyChangedHandler;
    private Bitmap? _cachedOpaqueBitmap;
    private PixelSize _cachedOpaqueBitmapSize;
    private List<PixelRect>? _cachedOpaquePixelRects;
    private ScalableRegion? _cachedOpaqueRegion;
    private readonly List<Rect> _interactiveHitTestRegions = new();
    private Win32HitTestPassThrough? _hitTestPassThrough;

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

                QueueWindowRegionRefresh();
            });
        }

        // The floating sub-toolbar lives outside the content frame; keep the window's opaque region in sync
        // as it appears / changes category (width) so it isn't clipped by the OS region.
        var subToolbar = this.FindControl<Border>("SubToolbarBorder");
        if (subToolbar != null)
        {
            _subToolbarBoundsSubscription = subToolbar.GetObservable(Visual.BoundsProperty)
                .Subscribe(_ => QueueWindowRegionRefresh());
        }

        var mainBorder = this.FindControl<Border>("MainBorder");
        if (mainBorder != null)
        {
            _mainBorderBoundsSubscription = mainBorder.GetObservable(Visual.BoundsProperty)
                .Subscribe(_ => QueueWindowRegionRefresh());
        }

        _windowBoundsSubscription = this.GetObservable(BoundsProperty)
            .Subscribe(_ => QueueWindowRegionRefresh());
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
            // The app's own confirmation dialog. The question used to borrow the update dialog, titled "GIMME UPDATE!!"
            // with an "update now" button, and messages showed Yes/No buttons whose answer nothing read.
            vm.ConfirmDialogAction ??= async (title, message) =>
            {
                var owner = TopLevel.GetTopLevel(this) as Window ?? this;
                return await GimmeCapture.Views.Dialogs.ConfirmationDialog.ShowConfirmation(
                    owner, title, message, GimmeCapture.Views.Dialogs.ConfirmationMode.YesNo)
                    == GimmeCapture.Views.Dialogs.ConfirmationResult.Yes;
            };

            vm.ShowDialogAction ??= (title, message) =>
            {
                var owner = TopLevel.GetTopLevel(this) as Window ?? this;
                GimmeCapture.Views.Dialogs.ConfirmationDialog.ShowConfirmation(
                    owner, title, message, GimmeCapture.Views.Dialogs.ConfirmationMode.OkOnly).Forget("FloatingImage.Message");
            };

            // Every pin saves through its own window, as a video pin does. Only a pin the overlay opened used to be
            // given a save, so Save did nothing on a pin opened from a pin or from the Compress editor's freeze-frame.
            vm.SaveAction ??= () => SaveImageToPickedFileAsync(vm);

            vm.OpenPinWindowAction ??= (bitmap, rect, color, thickness, runAI, initialInteractive, pinnedText, inferredFontSize, scrollableContentSize) =>
            {
                // Pin-from-pin (selecting a region inside a pin) is never a scrolling pin, so the content size is ignored.
                var newVm = new FloatingImageViewModel(bitmap, rect.Width, rect.Height, color, thickness, vm.HidePinDecoration, vm.HidePinBorder,
                    vm.ClipboardService, vm.AIResourceService, vm.SAM2RuntimeService, vm.AppSettingsService, vm.AIPathService, vm.ResourceQueue, pinnedText, inferredFontSize);
                
                newVm.WingScale = vm.WingScale;
                
                var newWin = new FloatingImageWindow
                {
                    DataContext = newVm,
                    Position = new PixelPoint(Position.X + 40, Position.Y + 40)
                };
                
                newWin.Show();
                
                if (runAI)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                        newVm.RemoveBackgroundCommand.Execute().SubscribeLoggingErrors();
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
                    QueueWindowRegionRefresh();
                }
            };
            vm.PropertyChanged += _boundViewModelPropertyChangedHandler;
            _boundViewModel = vm;
        }
    }

    // Asks where to save the pin's image, defaulting to the format picked in its toolbar, and writes it there. The
    // save command has swapped in the image with its annotations drawn in by the time this reads it.
    private async System.Threading.Tasks.Task SaveImageToPickedFileAsync(FloatingImageViewModel vm)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider is { } storageProvider)
            {
                // Default the dialog to the format chosen in the pin's toolbar (PNG/JPG/WebP).
                var (choices, defaultExt) = GimmeCapture.Views.Shared.VideoFilePickerTypes
                    .SaveImageChoicesPreferring(vm.SelectedImageFormat);
                var file = await storageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "Save Pinned Image",
                    DefaultExtension = defaultExt,
                    ShowOverwritePrompt = true,
                    SuggestedFileName = $"{CaptureFileNameService.SuggestedBaseName(vm.AppSettingsService?.Settings.FileNameTemplate)}.{defaultExt}",
                    FileTypeChoices = choices
                });

                if (file != null)
                {
                    // Encode to the ACTUALLY chosen extension (the dialog can override the toolbar
                    // default) via SkiaSharp, since Avalonia's Bitmap.Save only writes PNG.
                    string ext = System.IO.Path.GetExtension(file.Name).TrimStart('.').ToLowerInvariant();
                    var (skFormat, quality) = ext switch
                    {
                        "jpg" or "jpeg" => (SkiaSharp.SKEncodedImageFormat.Jpeg, 92),
                        "webp" => (SkiaSharp.SKEncodedImageFormat.Webp, 90),
                        _ => (SkiaSharp.SKEncodedImageFormat.Png, 100)
                    };
                    // Encoded off the UI thread: a 4K PNG takes over 2 s at full compression.
                    var image = vm.Image;
                    var (encoded, bytes, encErr) = await System.Threading.Tasks.Task.Run(() =>
                        (FloatingBitmapConversionHelper.TryEncodeBitmap(
                            image, skFormat, quality, out var b, out var e), b, e));
                    if (encoded)
                    {
                        using (var stream = new System.IO.FileStream(file.Path.LocalPath, System.IO.FileMode.Create))
                        {
                            await stream.WriteAsync(bytes, 0, bytes.Length);
                        }
                        FileLocationService.RevealInFileExplorer(file.Path.LocalPath);
                    }
                    else
                    {
                        AppLog.Debug("FloatingImage.Save", $"Failed to encode pinned image: {encErr}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("FloatingImage.Save", ex);
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
        _subToolbarBoundsSubscription?.Dispose();
        _subToolbarBoundsSubscription = null;
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

    /// <summary>How many times the window region was rebuilt, and the time that took. For the resize probe test.</summary>
    internal int WindowRegionRebuilds { get; private set; }

    internal TimeSpan WindowRegionRebuildTime { get; private set; }

    private bool _windowRegionRefreshQueued;

    /// <summary>
    /// Rebuilds the window region once the frame's layout is done. The view model's size, the window's, the content
    /// border's and the toolbars' bounds all ask for it, several of them in one frame and the first ones before the
    /// layout has caught up: a drag-resize rebuilt it 31 times a frame. Loaded priority runs after the layout and
    /// render, so the one rebuild sees every change with the bounds it ended at.
    /// </summary>
    private void QueueWindowRegionRefresh()
    {
        if (_windowRegionRefreshQueued)
        {
            return;
        }

        _windowRegionRefreshQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () =>
            {
                _windowRegionRefreshQueued = false;
                RefreshWindowRegion();
            },
            Avalonia.Threading.DispatcherPriority.Loaded);
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

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            RebuildWindowRegion(hwnd);
        }
        finally
        {
            WindowRegionRebuilds++;
            WindowRegionRebuildTime += System.Diagnostics.Stopwatch.GetElapsedTime(started);
        }
    }

    private void RebuildWindowRegion(IntPtr hwnd)
    {
        var opaqueRects = new List<Rect>();
        _interactiveHitTestRegions.Clear();
        IntPtr imageRegion = AddImageOpaqueRects(opaqueRects);
        AddMainBorderFrameRects(opaqueRects);
        AddDecorationOpaqueRects(opaqueRects);
        AddSubtreeOpaqueRect("ResizeHandlesLayer", 2, opaqueRects, includeInHitTest: true);
        AddSubtreeOpaqueRect("PinnedTextOverlay", 2, opaqueRects, includeInHitTest: true);
        AddSubtreeOpaqueRect("SelectionCanvas", 2, opaqueRects, includeInHitTest: true);
        AddSubtreeOpaqueRect("ToolbarBorder", 4, opaqueRects, includeInHitTest: true);
        AddSubtreeOpaqueRect("SubToolbarBorder", 4, opaqueRects, includeInHitTest: true);

        if (opaqueRects.Count == 0 && imageRegion == IntPtr.Zero)
        {
            // Degenerate layout (e.g. a very tall image shrunk small enough that the rendered
            // image rect collapses): never drop the whole region, or the window becomes
            // invisible/unclickable. Fall back to a full-window region so it stays usable.
            var bounds = this.Bounds;
            if (DataContext is FloatingImageViewModel { Image: not null } && bounds.Width >= 1 && bounds.Height >= 1)
            {
                Win32Helpers.SetDisjointOpaqueRegions(
                    hwnd,
                    new List<Rect> { new Rect(0, 0, bounds.Width, bounds.Height) },
                    null);
            }
            else
            {
                Win32Helpers.ClearWindowRegion(hwnd);
            }

            return;
        }

        opaqueRects.Add(new Rect(0, 0, 1, 1));
        Win32Helpers.SetDisjointOpaqueRegions(hwnd, opaqueRects, null, imageRegion);
    }

    /// <summary>Adds the image's opaque part: as a region placed where the image is drawn, which it returns and the
    /// caller then owns, or as rects added to <paramref name="dest"/>, when it returns zero.</summary>
    private IntPtr AddImageOpaqueRects(List<Rect> dest)
    {
        if (this.FindControl<Image>("PinnedImage") is not Image imageControl
            || DataContext is not FloatingImageViewModel vm
            || vm.Image == null)
        {
            return IntPtr.Zero;
        }

        // A scrolling-capture pin shows an opaque screenshot filling a fixed viewport that clips the (much
        // larger) scrolling image, so the whole viewport is opaque + hit-testable. Mark the MainBorder rect
        // directly — a per-pixel scan of the full strip would be far too costly and would not reflect scroll.
        if (vm.ContentScrolls)
        {
            if (this.FindControl<Border>("MainBorder") is { } viewportBorder
                && viewportBorder.Bounds.Width > 0 && viewportBorder.Bounds.Height > 0
                && viewportBorder.TranslatePoint(new Point(0, 0), this) is { } vpTopLeft)
            {
                var vpRect = new Rect(vpTopLeft.X, vpTopLeft.Y, viewportBorder.Bounds.Width, viewportBorder.Bounds.Height);
                double sc = RenderScaling;
                dest.Add(new Rect(vpRect.X * sc, vpRect.Y * sc, vpRect.Width * sc, vpRect.Height * sc));
                _interactiveHitTestRegions.Add(vpRect);
            }

            return IntPtr.Zero;
        }

        var renderedRect = GetImageRenderedRect(imageControl);
        if (renderedRect.Width <= 0 || renderedRect.Height <= 0)
        {
            return IntPtr.Zero;
        }

        var imageTopLeft = imageControl.TranslatePoint(new Point(0, 0), this);
        if (!imageTopLeft.HasValue)
        {
            return IntPtr.Zero;
        }

        var imageWindowRect = new Rect(
            imageTopLeft.Value.X + renderedRect.X,
            imageTopLeft.Value.Y + renderedRect.Y,
            renderedRect.Width,
            renderedRect.Height);

        var bitmap = vm.Image;
        // The scan reads every pixel, so it runs once per image; a resize or zoom only rescales its result. It used
        // to rescan the whole bitmap whenever the drawn size changed — on every step of a drag-resize. The region of
        // its rects is built once per image too, and placed for each rebuild.
        if (_cachedOpaquePixelRects == null
            || !ReferenceEquals(_cachedOpaqueBitmap, bitmap)
            || _cachedOpaqueBitmapSize != bitmap.PixelSize)
        {
            _cachedOpaqueBitmap = bitmap;
            _cachedOpaqueBitmapSize = bitmap.PixelSize;
            _cachedOpaquePixelRects = ScanOpaquePixelRects(bitmap);
            _cachedOpaqueRegion = ScalableRegion.FromRects(_cachedOpaquePixelRects);
        }

        double scaling = RenderScaling;
        var imageRects = OpaqueRegionScanner.Map(
            _cachedOpaquePixelRects, bitmap.PixelSize.Width, bitmap.PixelSize.Height, imageWindowRect);
        _interactiveHitTestRegions.AddRange(imageRects);

        IntPtr placed = _cachedOpaqueRegion?.CreatePlaced(
            imageWindowRect.Width / bitmap.PixelSize.Width * scaling,
            imageWindowRect.Height / bitmap.PixelSize.Height * scaling,
            imageWindowRect.X * scaling,
            imageWindowRect.Y * scaling) ?? IntPtr.Zero;
        if (placed == IntPtr.Zero)
        {
            foreach (var rect in imageRects)
            {
                dest.Add(new Rect(rect.X * scaling, rect.Y * scaling, rect.Width * scaling, rect.Height * scaling));
            }
        }

        return placed;
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

    private static List<PixelRect> ScanOpaquePixelRects(Bitmap bitmap)
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
            return OpaqueRegionScanner.Scan(rented.AsSpan(0, stride * height), width, height, stride);
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
        if (hwnd == IntPtr.Zero || _hitTestPassThrough != null)
        {
            return;
        }

        _hitTestPassThrough = Win32HitTestPassThrough.Install(hwnd, IsHitTestPassThrough);
    }

    private void UninitializeWin32HitTestHook()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Only a window that still has its handle gets its procedure back.
        var hwnd = this.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            _hitTestPassThrough?.Dispose();
        }

        _hitTestPassThrough = null;
    }

    // Clicks go through outside the interactive regions (in DIPs) while there are any.
    private bool IsHitTestPassThrough(PixelPoint client)
    {
        if (_interactiveHitTestRegions.Count == 0)
        {
            return false;
        }

        double scaling = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var clientPoint = new Point(client.X / scaling, client.Y / scaling);
        return !_interactiveHitTestRegions.Any(rect => rect.Contains(clientPoint));
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

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
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

                    // Fire-and-forget: OnPointerReleased overrides a void member, so we cannot await here.
                    // .Forget() observes and logs faults instead of letting an async-void exception crash the app.
                    vm.HandlePointClickAsync(pixelX, pixelY, isPositive)
                        .Forget("FloatingImage.HandlePointClickRelease");
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
}
