using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Views.Main;

public partial class CompareWindow : Window
{
    public CompareWindow()
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

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is CompareViewModel vm)
        {
            _ = vm.InitializeAsync(); // encode the sample + show the first frame
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as CompareViewModel)?.Dispose(); // stop playback + delete the temp sample
    }
}
