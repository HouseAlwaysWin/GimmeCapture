using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Views.Main;

/// <summary>
/// The video-editing window: the batch queue beside its output settings (<see cref="CompressWorkspaceView"/>). It belongs
/// to no other window, so closing the main window to the tray leaves it up. Closing it lets go of its controls and nothing
/// else: the queue, and any encode running, live in the view model and are there at the next open.
/// </summary>
public partial class CompressWindow : Window
{
    // The read usually takes a blink; a progress bar shown for that long would only flash.
    private static readonly TimeSpan ProgressDelay = TimeSpan.FromMilliseconds(300);

    private bool _closed;

    public CompressWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Fits the window to the screen it is to open on, the one <paramref name="near"/> is on when given, and centres it
    /// there: its preferred size is taller than a laptop screen at 150% has room for.
    /// </summary>
    internal void PlaceOnScreenOf(Window? near)
    {
        Screen? screen = (near is null ? null : Screens.ScreenFromWindow(near)) ?? Screens.Primary;
        if (screen is null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        (Size size, PixelPoint position) = WindowPlacementMath.FitCentred(
            new Size(Width, Height), new Size(MinWidth, MinHeight), screen.WorkingArea, screen.Scaling);
        Width = size.Width;
        Height = size.Height;
        Position = position;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ProcessMemoryTrimService.NotifyActivity("compress-window-opened");
        ShowContentAsync().Forget("Compress.ShowWindow");
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed = true;
        // Nothing allocates in an idle app, so nothing would collect the window for a long time: the full trim does.
        ProcessMemoryTrimService.RequestIdleTrimAsync("compress-window-closed").Forget("MemoryTrim.CompressWindowClosed");
    }

    // The saved queue and settings are read from disk in the background at the first open, and the view is built only once
    // they are in: earlier, it would offer an empty queue to act on, and settings the restore then overwrites. A progress
    // bar holds its place meanwhile, visible once the read takes long enough to notice.
    private async Task ShowContentAsync()
    {
        if (DataContext is MainWindowViewModel vm)
        {
            Task ready = vm.EnsureCompressInitializedAsync();
            IDisposable? revealProgress = null;
            if (!ready.IsCompleted)
            {
                var progress = new ProgressBar
                {
                    IsVisible = false,
                    Width = 160,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Content = progress;
                revealProgress = DispatcherTimer.RunOnce(
                    () =>
                    {
                        progress.IsIndeterminate = true;
                        progress.IsVisible = true;
                    },
                    ProgressDelay);
            }

            try
            {
                await ready;
            }
            catch (Exception ex)
            {
                // Still show the window: whatever part of the restore failed is in the log.
                AppLog.Error("Compress.Initialize", ex);
            }
            finally
            {
                revealProgress?.Dispose();
            }
        }

        if (!_closed)
        {
            Content = new CompressWorkspaceView();
        }
    }
}
