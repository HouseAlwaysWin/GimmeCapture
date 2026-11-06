using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using GimmeCapture.Services.Platforms.Desktop;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Tests;

/// <summary>
/// The capture overlay's warm-up (<see cref="SnipWindowFactory.RehearseOverlay"/>): the unseen window is bound to a view
/// model of its own and laid out at a screen's size, then both are let go of, and nothing of either stays alive. In a
/// published build the first overlay of a run then came up in 123-180 ms instead of 218-266 (docs/OPTIMIZATION_BACKLOG.md).
/// Opt-in (OVERLAY_WARMUP_PROBE=1): it sets up a headless Avalonia application, which a process can do only once.
/// </summary>
public sealed class OverlayWarmUpTests
{
    [OptInFact("OVERLAY_WARMUP_PROBE=1")]
    public void TheWarmUp_BindsAndLaysOutTheOverlay_ThenLeavesNothingAlive()
    {
        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var (window, viewModel, bound, laidOut) = WarmUp();
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.True(bound, "The window was never bound to a view model.");
        Assert.True(laidOut.Width > 100 && laidOut.Height > 100, $"Laid out at {laidOut}.");
        Assert.False(window.IsAlive, "The warm-up's window is still reachable.");
        Assert.False(viewModel.IsAlive, "The warm-up's view model is still reachable.");
    }

    // In a method of its own, so no local of the test keeps the window or its view model.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Window, WeakReference ViewModel, bool Bound, Size LaidOut) WarmUp()
    {
        var window = new SnipWindow { IsWarmUp = true };
        WeakReference viewModel = new(null);
        bool bound = false;
        bool closed = false;
        Size laidOut = default;
        window.DataContextChanged += (_, _) =>
        {
            if (window.DataContext is SnipWindowViewModel vm)
            {
                viewModel = new WeakReference(vm);
                bound = true;
            }
            else if (bound)
            {
                laidOut = window.Bounds.Size;
            }
        };
        window.Closed += (_, _) => closed = true;

        SnipWindowFactory.RehearseOverlay(window, () => false);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!closed && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Assert.True(closed, "The warm-up never closed its window.");
        Assert.Null(window.DataContext);
        return (new WeakReference(window), viewModel, bound, laidOut);
    }
}
