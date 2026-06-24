using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Infrastructure;
using System;
using System.Threading.Tasks;
using System.IO;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using System.Collections.Generic;
using ReactiveUI;

namespace GimmeCapture.Views.Floating;

public partial class FloatingVideoWindow : FloatingWindowBase
{
    public FloatingVideoWindow()
    {
        InitializeComponent();
        // Base constructor handles shared pointer and toolbar edge placement.
    }
    
    protected override Control? GetContentControl() => this.FindControl<Image>("PinnedVideo");

    protected override Bitmap? GetContentSnapshot()
    {
        if (DataContext is FloatingVideoViewModel vm && vm.VideoBitmap is { } videoBitmap)
        {
            try 
            {
                 using var locked = videoBitmap.Lock();
                 var clone = new WriteableBitmap(videoBitmap.PixelSize, videoBitmap.Dpi, videoBitmap.Format, videoBitmap.AlphaFormat);
                 using (var destLock = clone.Lock())
                 {
                     unsafe { Buffer.MemoryCopy((void*)locked.Address, (void*)destLock.Address, (long)destLock.RowBytes * clone.PixelSize.Height, (long)locked.RowBytes * videoBitmap.PixelSize.Height); }
                 }
                 return clone;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error snapshotting video: {ex.Message}");
                return null;
            }
        }
        return null;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FloatingVideoViewModel vm)
        {
            // Video Specific VM Setup
            vm.RequestRedraw = () => 
            {
                var image = GetContentControl();
                image?.InvalidateVisual();
            };

            // FIX: Ensure the ViewModel uses THIS window's StorageProvider for saving files.
            vm.PickSaveFileAction = async () =>
            {
                var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(this);
                if (topLevel == null) return null;

                var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = GimmeCapture.Services.Core.Infrastructure.LocalizationService.Instance["SaveVideo"],
                    DefaultExtension = System.IO.Path.GetExtension(vm.VideoPath).TrimStart('.'),
                    ShowOverwritePrompt = true,
                    SuggestedFileName = CaptureFileNameService.SuggestedBaseName(),
                    FileTypeChoices = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("Video Files") { Patterns = new[] { "*.mp4", "*.mkv", "*.gif", "*.webm", "*.mov" } },
                        new Avalonia.Platform.Storage.FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
                    }
                });

                return file?.Path.LocalPath;
            };

            // Self-wire sibling-pin creation so Crop/PinSelection can open a new
            // floating video window. The new window's OnDataContextChanged re-wires
            // this delegate, so cropped windows can be cropped again.
            vm.OpenPinnedVideoWindowAction ??= (recordingPath, pixelWidth, pixelHeight, originalWidth, originalHeight, color, thickness, hideDecoration, hideBorder) =>
            {
                var newVm = new FloatingVideoViewModel(
                    recordingPath,
                    string.Empty,
                    pixelWidth,
                    pixelHeight,
                    originalWidth,
                    originalHeight,
                    color,
                    thickness,
                    hideDecoration,
                    hideBorder,
                    vm.ClipboardService,
                    vm.AppSettingsService);

                newVm.WingScale = vm.WingScale;

                var padding = newVm.WindowPadding;
                var newWin = new FloatingVideoWindow
                {
                    DataContext = newVm,
                    Width = originalWidth + padding.Left + padding.Right,
                    Height = originalHeight + padding.Top + padding.Bottom,
                    Position = new PixelPoint(Position.X + 40, Position.Y + 40)
                };
                newWin.Show();
            };

            // 時間軸（多段）初始化
            InitializeSegmentStrip(vm);
        }
    }

    private bool _disposeStarted;

    // ── 時間軸（多段）邏輯：依片段長度按比例排版 + 播放點 ──
    private Grid? _segmentStripGrid;
    private Avalonia.Controls.Shapes.Rectangle? _segmentPlayhead;
    private IDisposable? _segmentSubscription;
    private Action? _segmentLayoutHandler;
    private FloatingVideoViewModel? _segmentVm;
    private bool _scrubbingSegmentStrip;

    private void InitializeSegmentStrip(FloatingVideoViewModel vm)
    {
        _segmentStripGrid = this.FindControl<Grid>("SegmentStripGrid");
        _segmentPlayhead = this.FindControl<Avalonia.Controls.Shapes.Rectangle>("SegmentPlayhead");
        if (_segmentStripGrid == null) return;

        _segmentVm = vm;
        _segmentLayoutHandler = () => Dispatcher.UIThread.Post(UpdateSegmentLayout);
        vm.SegmentLayoutChanged += _segmentLayoutHandler;

        // Recompute on playhead move, mode toggle, or duration arriving.
        _segmentSubscription = vm.WhenAnyValue(
            x => x.CurrentTimeSeconds,
            x => x.IsTimelineMode,
            x => x.TotalDuration)
            .Subscribe(_ => Dispatcher.UIThread.Post(UpdateSegmentLayout));

        // Recompute pixel widths when the strip is resized.
        _segmentStripGrid.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == "Bounds")
                Dispatcher.UIThread.Post(UpdateSegmentLayout);
        };

        // The strip = the SOURCE timeline. DRAG scrubs (playhead follows the finger); a TAP (no real
        // drag) toggles whether the piece under it is kept in the output.
        _segmentStripGrid.PointerPressed += OnSegmentStripPointerPressed;
        _segmentStripGrid.PointerMoved += OnSegmentStripPointerMoved;
        _segmentStripGrid.PointerReleased += OnSegmentStripPointerReleased;
    }

    private double _segmentPressX;
    private bool _segmentDidDrag;

    private void OnSegmentStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not FloatingVideoViewModel vm || _segmentStripGrid == null) return;
        _scrubbingSegmentStrip = true;
        _segmentDidDrag = false;
        _segmentPressX = e.GetPosition(_segmentStripGrid).X;
        e.Pointer.Capture(_segmentStripGrid);
        ScrubSegmentStripTo(vm, _segmentPressX);
    }

    private void OnSegmentStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_scrubbingSegmentStrip || _segmentStripGrid == null) return;
        if (DataContext is not FloatingVideoViewModel vm) return;
        double x = e.GetPosition(_segmentStripGrid).X;
        if (Math.Abs(x - _segmentPressX) > 4) _segmentDidDrag = true; // past threshold = a scrub, not a tap
        ScrubSegmentStripTo(vm, x);
    }

    private void OnSegmentStripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _scrubbingSegmentStrip = false;
        e.Pointer.Capture(null);

        // A tap (no real drag) toggles the kept/dropped state of the piece under the pointer.
        if (!_segmentDidDrag && DataContext is FloatingVideoViewModel vm && _segmentStripGrid != null)
        {
            double trackWidth = _segmentStripGrid.Bounds.Width;
            double total = vm.TotalOutputDuration;
            if (trackWidth > 0 && total > 0)
            {
                double sourceTime = Math.Clamp(_segmentPressX / trackWidth, 0, 1) * total;
                int index = GimmeCapture.Services.Core.Media.VideoSegmentEditor.IndexForSourceTime(
                    vm.EditSegments, sourceTime);
                vm.ToggleKept(index);
            }
        }
    }

    // Pieces are contiguous in source, so pixel X maps to source time directly. Seeking there pauses +
    // debounce-seeks the player and moves the red playhead.
    private void ScrubSegmentStripTo(FloatingVideoViewModel vm, double localX)
    {
        if (_segmentStripGrid == null) return;
        double trackWidth = _segmentStripGrid.Bounds.Width;
        double total = vm.TotalOutputDuration;
        if (trackWidth <= 0 || total <= 0) return;

        vm.CurrentTimeSeconds = Math.Clamp(localX / trackWidth, 0, 1) * total;

        // The CurrentTimeSeconds setter doesn't raise its own PropertyChanged (only the playback loop
        // does), so WhenAnyValue(CurrentTimeSeconds) won't fire during a scrub. Refresh the playhead
        // directly so the red bar tracks the finger live.
        UpdateSegmentLayout();
    }

    private void UpdateSegmentLayout()
    {
        if (DataContext is not FloatingVideoViewModel vm || _segmentStripGrid == null) return;
        if (!vm.IsTimelineMode) return;

        double trackWidth = _segmentStripGrid.Bounds.Width;
        double total = vm.TotalOutputDuration;
        if (trackWidth <= 0 || total <= 0) return;

        const double gap = 2; // visual seam between adjacent blocks
        foreach (SegmentBlockViewModel b in vm.SegmentBlocks)
        {
            b.PixelLeft = (b.OutputStart / total) * trackWidth;
            b.PixelWidth = Math.Max(2, ((b.OutputDuration / total) * trackWidth) - gap);
        }

        if (_segmentPlayhead != null)
        {
            double x = Math.Clamp((vm.CurrentTimeSeconds / total) * trackWidth, 0, trackWidth);
            _segmentPlayhead.RenderTransform = new Avalonia.Media.TranslateTransform(x, 0);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return;
        }

        if (_disposeStarted)
        {
            return;
        }

        _disposeStarted = true;
        _segmentSubscription?.Dispose();
        _segmentSubscription = null;
        if (_segmentVm is not null && _segmentLayoutHandler is not null)
        {
            _segmentVm.SegmentLayoutChanged -= _segmentLayoutHandler;
        }
        _segmentVm = null;
        _segmentLayoutHandler = null;

        if (DataContext is FloatingVideoViewModel vm)
        {
            vm.RequestRedraw = null;
            vm.BeginDispose().Forget("FloatingVideoWindow.DisposeOnClose");
        }
    }
}
