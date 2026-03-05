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
using System.Linq;
using System.Collections.Generic;
using ReactiveUI;

namespace GimmeCapture.Views.Floating;

public partial class FloatingVideoWindow : FloatingWindowBase
{
    public FloatingVideoWindow()
    {
        InitializeComponent();
        
        // Base constructor handles Event Handler registration
        
        PositionChanged += (s, e) => UpdateToolbarClamping();
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

            // 裁切拉桿初始化
            InitializeTrimThumbs(vm);
        }
    }

    private void UpdateToolbarClamping()
    {
        if (DataContext is FloatingVideoViewModel vm)
        {
            if (!vm.ShowToolbar) return;

            var screen = Screens.ScreenFromWindow(this);
            if (screen != null)
            {
                double scaling = screen.Scaling;
                
                // Position.Y is physical pixels. Bounds.Height is logical pixels.
                // WindowBottom = Physical Top + (Logical Height * Scaling)
                double windowBottomPhysical = Position.Y + (Bounds.Height * scaling);
                double screenBottomPhysical = screen.WorkingArea.Bottom;

                // Default Margin in VM is 10.
                double defaultBottomMargin = 10;
                
                // If Window Bottom is below Screen Bottom, we need to push the toolbar UP.
                if (windowBottomPhysical > screenBottomPhysical)
                {
                    double overlapPhysical = windowBottomPhysical - screenBottomPhysical;
                    double overlapLogical = overlapPhysical / scaling;
                    
                    double newBottomMargin = defaultBottomMargin + overlapLogical;
                    
                    // Cap it so it doesn't fly away (e.g. max window height - buffer)
                    if (newBottomMargin > Bounds.Height - 50) newBottomMargin = Bounds.Height - 50;

                    vm.ToolbarMargin = new Avalonia.Thickness(0, 0, 0, newBottomMargin);
                }
                else
                {
                    // Reset to default if fully on screen
                    if (vm.ToolbarMargin.Bottom != defaultBottomMargin)
                    {
                         vm.ToolbarMargin = new Avalonia.Thickness(0, 0, 0, defaultBottomMargin);
                    }
                }
            }
        }
    }
    // ── 裁切拉桿邏輯 ──
    private Grid? _trimTrackGrid;
    private Thumb? _trimStartThumb;
    private Thumb? _trimEndThumb;
    private IDisposable? _trimSubscription;

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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _trimSubscription?.Dispose();
        if (DataContext is FloatingVideoViewModel vm)
        {
            vm.Dispose();
        }
        base.OnClosing(e);
    }
}
