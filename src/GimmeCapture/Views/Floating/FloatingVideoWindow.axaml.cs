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

            // 裁切拉桿初始化
            InitializeTrimThumbs(vm);
            // 時間軸（多段）初始化
            InitializeSegmentStrip(vm);
        }
    }

    // ── 裁切拉桿邏輯 ──
    private Grid? _trimTrackGrid;
    private Thumb? _trimStartThumb;
    private Thumb? _trimEndThumb;
    private IDisposable? _trimSubscription;
    private bool _disposeStarted;

    private void InitializeTrimThumbs(FloatingVideoViewModel vm)
    {
        _trimTrackGrid = this.FindControl<Grid>("TrimTrackGrid");
        _trimStartThumb = this.FindControl<Thumb>("TrimStartThumb");
        _trimEndThumb = this.FindControl<Thumb>("TrimEndThumb");

        if (_trimStartThumb == null || _trimEndThumb == null || _trimTrackGrid == null) return;

        _trimStartThumb.DragDelta += OnTrimStartDragDelta;
        _trimEndThumb.DragDelta += OnTrimEndDragDelta;

        // 監聽屬性變更 → 更新 Thumb 位置
        _trimSubscription = vm.WhenAnyValue(
            x => x.TrimStartSeconds,
            x => x.TrimEndSeconds,
            x => x.TotalDuration,
            x => x.IsTrimmingMode)
            .Subscribe(_ => Dispatcher.UIThread.Post(UpdateTrimThumbPositions));

        // 尺寸變更時也更新位置
        _trimTrackGrid.PropertyChanged += (s, e) =>
        {
            if (e.Property.Name == "Bounds")
                Dispatcher.UIThread.Post(UpdateTrimThumbPositions);
        };
    }

    private void OnTrimStartDragDelta(object? sender, VectorEventArgs e)
    {
        if (DataContext is not FloatingVideoViewModel vm || _trimTrackGrid == null) return;

        double trackWidth = _trimTrackGrid.Bounds.Width - 12; // 扣除 Thumb 寬度
        double totalSec = vm.TotalDuration.TotalSeconds;
        if (totalSec <= 0 || trackWidth <= 0) return;

        double pixelsPerSecond = trackWidth / totalSec;
        double deltaSec = e.Vector.X / pixelsPerSecond;
        double newValue = vm.TrimStartSeconds + deltaSec;

        // 約束：不超過 end - 0.1，不低於 0
        newValue = Math.Max(0, Math.Min(newValue, vm.TrimEndSeconds - 0.1));
        vm.TrimStartSeconds = newValue;
    }

    private void OnTrimEndDragDelta(object? sender, VectorEventArgs e)
    {
        if (DataContext is not FloatingVideoViewModel vm || _trimTrackGrid == null) return;

        double trackWidth = _trimTrackGrid.Bounds.Width - 12;
        double totalSec = vm.TotalDuration.TotalSeconds;
        if (totalSec <= 0 || trackWidth <= 0) return;

        double pixelsPerSecond = trackWidth / totalSec;
        double deltaSec = e.Vector.X / pixelsPerSecond;
        double newValue = vm.TrimEndSeconds + deltaSec;

        // 約束：不低於 start + 0.1，不超過總時長
        newValue = Math.Max(vm.TrimStartSeconds + 0.1, Math.Min(newValue, totalSec));
        vm.TrimEndSeconds = newValue;
    }

    private void UpdateTrimThumbPositions()
    {
        if (DataContext is not FloatingVideoViewModel vm) return;
        if (_trimTrackGrid == null || _trimStartThumb == null || _trimEndThumb == null) return;
        if (!vm.IsTrimmingMode) return;

        double trackWidth = _trimTrackGrid.Bounds.Width - 12; // 可用滑動寬度
        double totalSec = vm.TotalDuration.TotalSeconds;
        if (totalSec <= 0 || trackWidth <= 0) return;

        double startX = (vm.TrimStartSeconds / totalSec) * trackWidth;
        double endX = (vm.TrimEndSeconds / totalSec) * trackWidth;

        _trimStartThumb.RenderTransform = new Avalonia.Media.TranslateTransform(startX, 0);
        _trimEndThumb.RenderTransform = new Avalonia.Media.TranslateTransform(endX, 0);
    }

    // ── 時間軸（多段）邏輯：依片段長度按比例排版 + 播放點 ──
    private Grid? _segmentStripGrid;
    private Avalonia.Controls.Shapes.Rectangle? _segmentPlayhead;
    private IDisposable? _segmentSubscription;
    private Action? _segmentLayoutHandler;
    private FloatingVideoViewModel? _segmentVm;

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
            double outSec = vm.CurrentTimeSeconds;
            if (GimmeCapture.Services.Core.Media.VideoSegmentEditor.TryMapSourceToOutput(
                    vm.EditSegments, vm.CurrentTimeSeconds, out double mapped))
            {
                outSec = mapped;
            }

            double x = Math.Clamp((outSec / total) * trackWidth, 0, trackWidth);
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
        _trimSubscription?.Dispose();
        _trimSubscription = null;
        _segmentSubscription?.Dispose();
        _segmentSubscription = null;
        if (_segmentVm is not null && _segmentLayoutHandler is not null)
        {
            _segmentVm.SegmentLayoutChanged -= _segmentLayoutHandler;
        }
        _segmentVm = null;
        _segmentLayoutHandler = null;
        if (_trimStartThumb is not null)
        {
            _trimStartThumb.DragDelta -= OnTrimStartDragDelta;
        }
        if (_trimEndThumb is not null)
        {
            _trimEndThumb.DragDelta -= OnTrimEndDragDelta;
        }

        if (DataContext is FloatingVideoViewModel vm)
        {
            vm.RequestRedraw = null;
            vm.BeginDispose().Forget("FloatingVideoWindow.DisposeOnClose");
        }
    }
}
