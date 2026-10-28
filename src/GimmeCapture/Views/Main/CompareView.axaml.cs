using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Views.Main;

// Inline before/after quality-compare panel hosted in the editor preview (DataContext = CompareViewModel).
// Replaces the former standalone CompareWindow; the editor VM drives its lifecycle (InitializeAsync on show,
// Dispose on close).
public partial class CompareView : UserControl
{
    // The two frame Images (original | compressed). The view model draws each new frame into the bitmap an Image
    // already shows, which repaints nothing until the Image is invalidated.
    private readonly Image[] _frameImages;
    private CompareViewModel? _redrawSource;

    public CompareView()
    {
        InitializeComponent();
        _frameImages = this.GetLogicalDescendants().OfType<Image>().ToArray();

        // Scrub: pause playback when the position slider is grabbed, seek to the dropped position on release.
        PositionSlider.AddHandler(PointerPressedEvent, OnScrubPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        PositionSlider.AddHandler(PointerReleasedEvent, OnScrubReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // The editor swaps compare view models in and out of one CompareView, so the hook follows the DataContext.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_redrawSource != null)
        {
            _redrawSource.RequestRedraw = null;
        }

        _redrawSource = DataContext as CompareViewModel;
        if (_redrawSource != null)
        {
            _redrawSource.RequestRedraw = RedrawFrames;
        }
    }

    private void RedrawFrames()
    {
        foreach (Image image in _frameImages)
        {
            image.InvalidateVisual();
        }
    }

    private void OnScrubPressed(object? sender, PointerPressedEventArgs e)
        => (DataContext as CompareViewModel)?.BeginScrub();

    private void OnScrubReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is CompareViewModel vm)
        {
            _ = vm.SeekAsync(PositionSlider.Value);
        }
    }
}
