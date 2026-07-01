using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.ViewModels.Main;
using ReactiveUI;

namespace GimmeCapture.Views.Main;

public partial class TrimWindow : Window
{
    private IDisposable? _positionSub;
    private Action? _layoutHandler;
    private double _stripPressX;
    private bool _stripDidDrag;
    private bool _stripScrubbing;

    public TrimWindow()
    {
        InitializeComponent();

        // Playback scrubber: pause on grab, seek to the dropped position on release.
        PositionSlider.AddHandler(PointerPressedEvent, OnScrubPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        PositionSlider.AddHandler(PointerReleasedEvent, OnScrubReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        // Segment strip: drag = scrub the playhead, tap = keep/drop the piece under it.
        SegmentStripGrid.PointerPressed += OnStripPressed;
        SegmentStripGrid.PointerMoved += OnStripMoved;
        SegmentStripGrid.PointerReleased += OnStripReleased;
        SegmentStripGrid.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Bounds")
            {
                Dispatcher.UIThread.Post(UpdateSegmentLayout);
            }
        };
    }

    private void OnScrubPressed(object? sender, PointerPressedEventArgs e)
        => (DataContext as TrimViewModel)?.BeginScrub();

    private void OnScrubReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is TrimViewModel vm)
        {
            _ = vm.SeekAsync(PositionSlider.Value);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is TrimViewModel vm)
        {
            vm.RequestClose = Close;
            _layoutHandler = () => Dispatcher.UIThread.Post(UpdateSegmentLayout);
            vm.SegmentLayoutChanged += _layoutHandler;
            _positionSub = vm.WhenAnyValue(x => x.PositionSeconds)
                .Subscribe(_ => Dispatcher.UIThread.Post(UpdateSegmentLayout));
            UpdateSegmentLayout();
            _ = vm.InitializeAsync(); // decode + show the first frame
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _positionSub?.Dispose();
        if (DataContext is TrimViewModel vm)
        {
            if (_layoutHandler != null)
            {
                vm.SegmentLayoutChanged -= _layoutHandler;
            }
            vm.Dispose(); // stop playback
        }
    }

    // ── Segment strip: drag = scrub, tap = keep/drop (mirrors FloatingVideoWindow) ──

    private void OnStripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not TrimViewModel vm)
        {
            return;
        }
        _stripScrubbing = true;
        _stripDidDrag = false;
        _stripPressX = e.GetPosition(SegmentStripGrid).X;
        e.Pointer.Capture(SegmentStripGrid);
        vm.BeginScrub();
        ScrubStripTo(vm, _stripPressX);
    }

    private void OnStripMoved(object? sender, PointerEventArgs e)
    {
        if (!_stripScrubbing || DataContext is not TrimViewModel vm)
        {
            return;
        }
        double x = e.GetPosition(SegmentStripGrid).X;
        if (Math.Abs(x - _stripPressX) > 4)
        {
            _stripDidDrag = true; // past threshold = a scrub, not a tap
        }
        ScrubStripTo(vm, x);
    }

    private void OnStripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _stripScrubbing = false;
        e.Pointer.Capture(null);
        if (DataContext is not TrimViewModel vm)
        {
            return;
        }

        if (_stripDidDrag)
        {
            _ = vm.SeekAsync(vm.PositionSeconds); // decode the frame at the scrubbed position
            return;
        }

        // A tap (no real drag) toggles the kept/dropped state of the piece under the pointer.
        double w = SegmentStripGrid.Bounds.Width;
        double total = vm.TotalSourceDuration;
        if (w > 0 && total > 0)
        {
            double sourceTime = Math.Clamp(_stripPressX / w, 0, 1) * total;
            int index = VideoSegmentEditor.IndexForSourceTime(vm.EditSegments, sourceTime);
            vm.ToggleKept(index);
        }
    }

    // Pieces are contiguous in source, so pixel X maps to source time directly; move the red playhead.
    private void ScrubStripTo(TrimViewModel vm, double localX)
    {
        double w = SegmentStripGrid.Bounds.Width;
        double total = vm.TotalSourceDuration;
        if (w <= 0 || total <= 0)
        {
            return;
        }
        vm.PositionSeconds = Math.Clamp(localX / w, 0, 1) * total;
        UpdateSegmentLayout();
    }

    private void UpdateSegmentLayout()
    {
        if (DataContext is not TrimViewModel vm)
        {
            return;
        }
        double w = SegmentStripGrid.Bounds.Width;
        double total = vm.TotalSourceDuration;
        if (w <= 0 || total <= 0)
        {
            return;
        }

        const double gap = 2; // visual seam between adjacent blocks
        foreach (SegmentBlockViewModel b in vm.SegmentBlocks)
        {
            b.PixelLeft = (b.OutputStart / total) * w;
            b.PixelWidth = Math.Max(2, ((b.OutputDuration / total) * w) - gap);
        }

        double x = Math.Clamp((vm.PositionSeconds / total) * w, 0, w);
        SegmentPlayhead.RenderTransform = new TranslateTransform(x, 0);
    }
}
