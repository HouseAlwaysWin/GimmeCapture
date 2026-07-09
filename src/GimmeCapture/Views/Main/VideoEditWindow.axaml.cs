using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Media;
using GimmeCapture.ViewModels.Floating;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Shared;
using ReactiveUI;

namespace GimmeCapture.Views.Main;

public partial class VideoEditWindow : Window
{
    private IDisposable? _positionSub;
    private Action? _layoutHandler;
    private double _stripPressX;
    private bool _stripDidDrag;
    private bool _stripScrubbing;
    private int _stripPressClickCount;

    private bool _cropDragging;
    private Point _cropStart;

    // Redaction selection marquee (surface coords)
    private bool _marqueeActive;
    private Point _marqueeStart;

    // Shared annotation pointer state machine (same one the Pin windows use)
    private readonly AnnotationInputController _annotationInput;

    public VideoEditWindow()
    {
        InitializeComponent();

        _annotationInput = new AnnotationInputController(
            getState: () => (DataContext as VideoEditViewModel)?.EditorState,
            getContentControl: () => SurfacePanel,
            getContentSnapshot: () => (DataContext as VideoEditViewModel)?.Frame,
            isInteractionBlocked: () => (DataContext as VideoEditViewModel)?.IsPreparing ?? true,
            confirmTextEntry: () =>
            {
                if (DataContext is VideoEditViewModel vm)
                {
                    vm.Draw.ConfirmTextEntryCommand.Execute(System.Reactive.Unit.Default).Subscribe();
                }
            },
            focusTextInput: () => EditTextEntry?.FocusTextInput(),
            // Capture to the surface panel (its own PointerMoved/Released handlers drive the state machine).
            // Capturing to the window would redirect events away from the panel and strand mid-drag draws.
            captureTarget: SurfacePanel,
            setCursor: c => Cursor = c);

        // Preview surface: annotation drawing/selection OR the redaction marquee, in surface coords.
        SurfacePanel.PointerPressed += OnSurfacePressed;
        SurfacePanel.PointerMoved += OnSurfaceMoved;
        SurfacePanel.PointerReleased += OnSurfaceReleased;

        KeyDown += OnEditorKeyDown;

        // Playback scrubber: pause on grab, seek to the dropped position on release.
        PositionSlider.AddHandler(PointerPressedEvent, OnScrubPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        PositionSlider.AddHandler(PointerReleasedEvent, OnScrubReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        // Segment strip: drag = scrub the playhead, double-click = keep/drop the piece under it.
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

        // Crop overlay: drag a rectangle over the (un-rotated) preview to set the crop.
        CropOverlay.PointerPressed += OnCropPressed;
        CropOverlay.PointerMoved += OnCropMoved;
        CropOverlay.PointerReleased += OnCropReleased;
    }

    // ── Preview surface: redaction marquee first (selection mode), else the annotation state machine ──

    private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not VideoEditViewModel vm)
        {
            return;
        }

        if (vm.IsSelectionMode && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _marqueeActive = true;
            _marqueeStart = ClampToSurface(e.GetPosition(SurfacePanel));
            vm.SelectionRect = new Rect(_marqueeStart, new Size(0, 0));
            e.Pointer.Capture(SurfacePanel);
            e.Handled = true;
            return;
        }

        _annotationInput.HandlePointerPressed(e);
    }

    private void OnSurfaceMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not VideoEditViewModel vm)
        {
            return;
        }

        if (_marqueeActive)
        {
            Point pos = ClampToSurface(e.GetPosition(SurfacePanel));
            vm.SelectionRect = new Rect(
                Math.Min(_marqueeStart.X, pos.X),
                Math.Min(_marqueeStart.Y, pos.Y),
                Math.Abs(pos.X - _marqueeStart.X),
                Math.Abs(pos.Y - _marqueeStart.Y));
            return;
        }

        _annotationInput.HandlePointerMoved(e);
    }

    private void OnSurfaceReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_marqueeActive)
        {
            _marqueeActive = false;
            e.Pointer.Capture(null);
            return;
        }

        _annotationInput.HandlePointerReleased(e);
    }

    private Point ClampToSurface(Point p) => new(
        Math.Clamp(p.X, 0, SurfacePanel.Bounds.Width),
        Math.Clamp(p.Y, 0, SurfacePanel.Bounds.Height));

    // ── Keys: Delete (remove selected annotation), Esc (cancel in priority order), Ctrl+Z/Y ──
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not VideoEditViewModel vm)
        {
            return;
        }

        if (vm.Draw.IsEnteringText)
        {
            if (e.Key == Key.Escape)
            {
                vm.Draw.CancelTextEntryCommand.Execute(System.Reactive.Unit.Default).Subscribe();
                e.Handled = true;
            }
            return; // typing — leave every other key to the TextBox
        }

        switch (e.Key)
        {
            case Key.Delete when vm.EditorState.SelectedAnnotation != null:
                vm.EditorState.RemoveSelectedAnnotation();
                e.Handled = true;
                break;
            case Key.Escape:
                if (vm.IsSelectionMode)
                {
                    vm.IsSelectionMode = false;
                }
                else if (vm.EditorState.SelectedAnnotation != null)
                {
                    vm.EditorState.ClearSelection();
                }
                else if (vm.EditorState.CurrentAnnotationTool != AnnotationType.None)
                {
                    vm.EditorState.CurrentAnnotationTool = AnnotationType.None;
                }
                e.Handled = true;
                break;
            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                vm.Draw.UndoCommand.Execute(System.Reactive.Unit.Default).Subscribe();
                e.Handled = true;
                break;
            case Key.Y when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                vm.Draw.RedoCommand.Execute(System.Reactive.Unit.Default).Subscribe();
                e.Handled = true;
                break;
        }
    }

    private void OnScrubPressed(object? sender, PointerPressedEventArgs e)
        => (DataContext as VideoEditViewModel)?.BeginScrub();

    private void OnScrubReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is VideoEditViewModel vm)
        {
            _ = vm.SeekAsync(PositionSlider.Value);
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is VideoEditViewModel vm)
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
        if (DataContext is VideoEditViewModel vm)
        {
            if (_layoutHandler != null)
            {
                vm.SegmentLayoutChanged -= _layoutHandler;
            }
            vm.Dispose(); // stop playback
        }
    }

    // ── Segment strip: drag = scrub, double-click = keep/drop (mirrors FloatingVideoWindow) ──

    private void OnStripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not VideoEditViewModel vm)
        {
            return;
        }
        _stripScrubbing = true;
        _stripDidDrag = false;
        _stripPressClickCount = e.ClickCount;
        _stripPressX = e.GetPosition(SegmentStripGrid).X;
        e.Pointer.Capture(SegmentStripGrid);
        vm.BeginScrub();
        ScrubStripTo(vm, _stripPressX);
    }

    private void OnStripMoved(object? sender, PointerEventArgs e)
    {
        if (!_stripScrubbing || DataContext is not VideoEditViewModel vm)
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
        if (DataContext is not VideoEditViewModel vm)
        {
            return;
        }

        // A DOUBLE-click (no drag) toggles keep/drop of the piece under the pointer. A single click or a drag just
        // scrubs — decode the frame at the landed position. Requiring a double-click keeps single clicks free for
        // seeking without accidentally flipping a segment's kept/dropped state (which shifts the whole strip).
        // `== 2` (not `>= 2`) so a triple/quadruple over-click fires exactly once — ClickCount keeps rising past 2.
        if (!_stripDidDrag && _stripPressClickCount == 2)
        {
            double w = SegmentStripGrid.Bounds.Width;
            double total = vm.TotalSourceDuration;
            if (w > 0 && total > 0)
            {
                double sourceTime = Math.Clamp(_stripPressX / w, 0, 1) * total;
                int index = VideoSegmentEditor.IndexForSourceTime(vm.EditSegments, sourceTime);
                vm.ToggleKept(index);
            }
            return;
        }

        _ = vm.SeekAsync(vm.PositionSeconds); // decode the frame at the scrubbed/clicked position
    }

    // Pieces are contiguous in source, so pixel X maps to source time directly; move the red playhead.
    private void ScrubStripTo(VideoEditViewModel vm, double localX)
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
        if (DataContext is not VideoEditViewModel vm)
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

    // ── Crop overlay: drag a rectangle; on release map it into the letterboxed video content rect → source crop ──

    private void OnCropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not VideoEditViewModel vm || !vm.IsCropMode)
        {
            return;
        }
        _cropDragging = true;
        _cropStart = e.GetPosition(CropOverlay);
        Canvas.SetLeft(CropRect, _cropStart.X);
        Canvas.SetTop(CropRect, _cropStart.Y);
        CropRect.Width = 0;
        CropRect.Height = 0;
        CropRect.IsVisible = true;
        e.Pointer.Capture(CropOverlay);
    }

    private void OnCropMoved(object? sender, PointerEventArgs e)
    {
        if (!_cropDragging)
        {
            return;
        }
        Point p = e.GetPosition(CropOverlay);
        Canvas.SetLeft(CropRect, Math.Min(p.X, _cropStart.X));
        Canvas.SetTop(CropRect, Math.Min(p.Y, _cropStart.Y));
        CropRect.Width = Math.Abs(p.X - _cropStart.X);
        CropRect.Height = Math.Abs(p.Y - _cropStart.Y);
    }

    private void OnCropReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_cropDragging)
        {
            return;
        }
        _cropDragging = false;
        e.Pointer.Capture(null);
        CropRect.IsVisible = false;
        if (DataContext is not VideoEditViewModel vm)
        {
            return;
        }

        double selX = Canvas.GetLeft(CropRect);
        double selY = Canvas.GetTop(CropRect);
        double selW = CropRect.Width;
        double selH = CropRect.Height;
        if (selW < 4 || selH < 4)
        {
            return;
        }

        // The overlay fills the Image cell; the video is letterboxed inside (Uniform). Find that content rect.
        double bw = PreviewImage.Bounds.Width, bh = PreviewImage.Bounds.Height;
        if (bw <= 0 || bh <= 0 || vm.SourceWidth <= 0 || vm.SourceHeight <= 0)
        {
            return;
        }
        double srcAspect = vm.SourceWidth / (double)vm.SourceHeight;
        double contentW, contentH;
        if (bw / bh > srcAspect) { contentH = bh; contentW = bh * srcAspect; }
        else { contentW = bw; contentH = bw / srcAspect; }
        double offX = (bw - contentW) / 2, offY = (bh - contentH) / 2;

        double relX = Math.Clamp(selX - offX, 0, contentW);
        double relY = Math.Clamp(selY - offY, 0, contentH);
        double relRight = Math.Clamp(selX + selW - offX, 0, contentW);
        double relBottom = Math.Clamp(selY + selH - offY, 0, contentH);
        double relW = relRight - relX, relH = relBottom - relY;
        if (relW >= 4 && relH >= 4)
        {
            _ = vm.SetCropFromSelectionAsync(relX, relY, relW, relH, contentW, contentH);
        }
    }
}
