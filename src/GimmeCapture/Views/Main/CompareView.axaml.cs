using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Views.Main;

// Inline before/after quality-compare panel hosted in the editor preview (DataContext = CompareViewModel).
// Replaces the former standalone CompareWindow; the editor VM drives its lifecycle (InitializeAsync on show,
// Dispose on close).
public partial class CompareView : UserControl
{
    public CompareView()
    {
        InitializeComponent();

        // Scrub: pause playback when the position slider is grabbed, seek to the dropped position on release.
        PositionSlider.AddHandler(PointerPressedEvent, OnScrubPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        PositionSlider.AddHandler(PointerReleasedEvent, OnScrubReleased, RoutingStrategies.Bubble, handledEventsToo: true);
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
